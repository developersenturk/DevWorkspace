using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal static class FlutterInstaller
{
    private static readonly Uri ReleaseRoot = new("https://storage.googleapis.com/flutter_infra_release/releases/");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    internal static async Task InstallAsync(IProgress<string> progress, CancellationToken token)
    {
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new IOException(Loc.Text("L33D59158CF11"))
        };
        string root = DevelopmentTools.ToolsFolder;
        string destination = Path.Combine(root, "flutter");
        if (Directory.Exists(destination))
            throw new IOException(Loc.Text("LE95A716BA0E3"));

        progress.Report(Loc.Text("L20FD1C4ED712"));
        using var releases = JsonDocument.Parse(await Client.GetStringAsync(new Uri(ReleaseRoot, "releases_windows.json"), token));
        var index = releases.RootElement;
        string? stable = index.GetProperty("current_release").GetProperty("stable").GetString();
        var release = index.GetProperty("releases").EnumerateArray().FirstOrDefault(item =>
            item.GetProperty("hash").GetString() == stable && item.GetProperty("channel").GetString() == "stable" &&
            (item.TryGetProperty("dart_sdk_arch", out var arch) ? arch.GetString() : "x64") == architecture);
        if (release.ValueKind == JsonValueKind.Undefined)
            throw new IOException(Loc.Text("LA92D10D68C72"));

        string archive = release.GetProperty("archive").GetString() ?? "";
        string checksum = release.GetProperty("sha256").GetString() ?? "";
        if (!Regex.IsMatch(archive, @"\Astable/windows/flutter_windows_[a-zA-Z0-9._-]+\.zip\z") ||
            !Regex.IsMatch(checksum, @"\A[0-9a-fA-F]{64}\z"))
            throw new IOException(Loc.Text("L8D4BCFAC4E29"));

        Directory.CreateDirectory(root);
        string staging = Path.Combine(root, $".flutter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string zipPath = Path.Combine(staging, "flutter.zip");
            using var download = await Client.GetAsync(new Uri(ReleaseRoot, archive), HttpCompletionOption.ResponseHeadersRead, token);
            download.EnsureSuccessStatusCode();
            long? size = download.Content.Headers.ContentLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = File.Create(zipPath))
            await using (var input = await download.Content.ReadAsStreamAsync(token))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int percent = -5;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    total += count;
                    int next = size > 0 ? (int)(100 * total / size.Value) : 0;
                    if (next >= percent + 5)
                    {
                        percent = next;
                        progress.Report(size > 0 ? Loc.Format("L42CD2A33570B", percent) : Loc.Text("LF4B63101017E"));
                    }
                }
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(checksum, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.Text("L0F6929F18DFF"));

            progress.Report(Loc.Text("LCCD926CD6675"));
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                string prefix = staging + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(Loc.Text("L6BCA64FF4019"));
                    if (entry.Name.Length == 0)
                        Directory.CreateDirectory(path);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        entry.ExtractToFile(path);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            string sdk = Path.Combine(staging, "flutter");
            if (!File.Exists(Path.Combine(sdk, "bin", "flutter.bat")))
                throw new IOException(Loc.Text("L25CE9143E103"));
            try { Directory.Move(sdk, destination); }
            catch (IOException) when (File.Exists(Path.Combine(destination, "bin", "flutter.bat"))) { }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
