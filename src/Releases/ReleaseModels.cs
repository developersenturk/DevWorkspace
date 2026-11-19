using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed record ReleaseProfile(string ProjectFile = "", string Repository = "", string AppId = "",
    string Version = "", string Notes = "", string TestProjects = "", bool RequireChecks = true);

internal sealed record ReleaseFile(string Name, long Size, string Sha256);
internal sealed record ReleaseDraftReference(long Id, string Repository, string Tag, string Commit);
internal sealed record ReleasePackage(string Directory, string Root, string ProjectFile, string Repository,
    string AppId, string Version, string Commit, DateTimeOffset Created, ReleaseFile[] Files)
{
    public string Label => $"{Repository} · {Version} · {Commit[..Math.Min(7, Commit.Length)]} · {Created.LocalDateTime:g}";
    public string Tag => "v" + Version;
    public string ManifestPath => Path.Combine(Directory, "package.json");
}

internal sealed record RemoteRelease(long Id, string Tag, string Name, string Target, bool Draft, bool Prerelease,
    string Url, DateTimeOffset? Published, ReleaseAsset[] Assets)
{
    public string State => ReleaseText.T(Draft ? "Draft" : Prerelease ? "Prerelease" : "Published");
    public long Downloads => Assets.Where(a => ReleaseRules.IsDownload(a.Name)).Sum(a => a.Downloads);
    public string PublishedLabel => Published?.LocalDateTime.ToString("g") ?? "—";
}
internal sealed record ReleaseAsset(long Id, string Name, string State, long Size, string? Digest, long Downloads, string Url)
{
    public string SizeLabel => $"{Size / 1048576d:N1} MB";
    public string Kind => ReleaseText.T(Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) ? "Installer" :
        Name.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase) ? "Portable" :
        Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ? "UpdatePackage" : "Feed");
}

internal static class ReleaseRules
{
    internal static void Version(string version)
    {
        if (!Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") ||
            !System.Version.TryParse(version, out _)) throw new IOException(ReleaseText.T("ValidVersion"));
    }
    internal static void AppId(string id)
    {
        if (!Regex.IsMatch(id, @"\A[A-Za-z][A-Za-z0-9.]{0,79}\z")) throw new IOException(ReleaseText.T("ValidId"));
    }
    internal static string Repository(string input)
    {
        string value = input.Trim();
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) value = value[15..];
        else if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) value = value[19..];
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        value = value.TrimEnd('/');
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\z"))
            throw new IOException(ReleaseText.T("InvalidRepository"));
        return value;
    }
    internal static bool IsDownload(string name) => name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);

    internal static void RequiredFiles(ReleasePackage package)
    {
        Version(package.Version);
        AppId(package.AppId);
        Repository(package.Repository);
        string[] names = [$"{package.AppId}-win-x64-Setup.exe", $"{package.AppId}-win-x64-Portable.zip",
            $"{package.AppId}-{package.Version}-win-x64-full.nupkg", "releases.win-x64.json", "RELEASES-win-x64"];
        if (names.Any(n => !package.Files.Any(f => f.Name == n)) || package.Files.Length != names.Length ||
            package.Files.Any(f => Path.GetFileName(f.Name) != f.Name || f.Size <= 0))
            throw new IOException(ReleaseText.T("IncompletePackage"));
    }
    internal static void MatchingAsset(ReleaseFile file, ReleaseAsset asset)
    {
        if (asset.State != "uploaded" || asset.Size != file.Size ||
            !string.Equals(asset.Digest, "sha256:" + file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(ReleaseText.T("AssetMismatch") + " " + file.Name);
    }
    internal static void SameDraft(ReleasePackage package, RemoteRelease release)
    {
        if (!release.Draft || release.Tag != package.Tag || release.Target != package.Commit)
            throw new IOException(ReleaseText.T("DraftMismatch"));
    }
    internal static string Csv(string value) => "\"" + (value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" : "") +
        value.Replace("\"", "\"\"") + "\"";
}

internal sealed class ReleaseStore(string? directory = null)
{
    internal string DirectoryPath { get; } = directory ?? Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "DevWorkspace", "ReleaseCenter");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal string NewPackageDirectory() => Path.Combine(DirectoryPath, "packages", Guid.NewGuid().ToString("N"));
    internal void SaveProfile(ReleaseProfile profile) => Save(Path.Combine(DirectoryPath, "profile.json"), profile);
    internal ReleaseProfile LoadProfile()
    {
        try { return JsonSerializer.Deserialize<ReleaseProfile>(File.ReadAllText(Path.Combine(DirectoryPath, "profile.json")), Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
    internal ReleasePackage[] Packages()
    {
        string root = Path.Combine(DirectoryPath, "packages");
        if (!Directory.Exists(root)) return [];
        var packages = new List<ReleasePackage>();
        foreach (string path in Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "package.json")).Where(File.Exists))
        {
            try
            {
                var package = JsonSerializer.Deserialize<ReleasePackage>(File.ReadAllText(path), Json);
                if (package is not null && Path.GetFullPath(package.ManifestPath) == Path.GetFullPath(path)) packages.Add(package);
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException) { }
        }
        return packages.OrderByDescending(p => p.Created).ToArray();
    }
    internal static async Task<ReleaseFile> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        return new(Path.GetFileName(path), stream.Length, hash);
    }
    internal static async Task VerifyAsync(ReleasePackage package, CancellationToken token)
    {
        ReleaseRules.RequiredFiles(package);
        foreach (var file in package.Files)
        {
            var actual = await HashAsync(Path.Combine(package.Directory, "releases", file.Name), token);
            if (actual != file) throw new IOException(ReleaseText.T("AssetMismatch") + " " + file.Name);
        }
    }
}
