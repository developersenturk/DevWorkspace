using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DevWorkspace;

internal sealed record ProjectCommand(string Name, string Executable, string Arguments = "")
{
    public string Display => $"{Executable} {Arguments}".TrimEnd();
}

internal static class ProjectTools
{
    public static IReadOnlyList<ProjectCommand> Presets(ProjectEntry project)
    {
        var actions = new List<ProjectCommand>();
        if (project.EntryFile is string entry && File.Exists(entry) && IsDotNetEntry(entry))
        {
            actions.Add(new("Testleri çalıştır", "dotnet.exe", $"test {Quote(entry)}"));
            foreach (string runnable in FindRunnableProjects(entry).Take(8))
                actions.Add(new($"Çalıştır · {Path.GetFileNameWithoutExtension(runnable)}", "dotnet.exe", $"run --project {Quote(runnable)}"));
        }

        string package = Path.Combine(project.Folder, "package.json");
        if (File.Exists(package))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(package));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("scripts", out var scripts)
                    && scripts.ValueKind == JsonValueKind.Object)
                {
                    if (scripts.TryGetProperty("test", out _))
                        actions.Add(new("Testleri çalıştır · npm", "cmd.exe", "/d /c npm.cmd test"));
                    if (scripts.TryGetProperty("dev", out _))
                        actions.Add(new("Geliştirme sunucusunu başlat", "cmd.exe", "/d /c npm.cmd run dev"));
                    else if (scripts.TryGetProperty("start", out _))
                        actions.Add(new("Uygulamayı başlat · npm", "cmd.exe", "/d /c npm.cmd start"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (File.Exists(Path.Combine(project.Folder, "pubspec.yaml")))
        {
            actions.Add(new("Testleri çalıştır · Flutter", "cmd.exe", "/d /c flutter.bat test"));
            actions.Add(new("Uygulamayı başlat · Flutter", "cmd.exe", "/d /c flutter.bat run"));
        }
        return actions;
    }

    public static bool IsDotNetEntry(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj" or ".vbproj" or ".fsproj";

    private static IReadOnlyList<string> FindRunnableProjects(string entry)
    {
        try
        {
            string[] candidates = [entry];
            if (Path.GetExtension(entry).ToLowerInvariant() is ".sln" or ".slnx")
            {
                var (paths, properties) = ProjectMovePlanner.ReadReferences(new(entry));
                candidates = paths.Select(path => ProjectMovePlanner.Resolve(path, entry, properties)).OfType<string>().ToArray();
            }

            return candidates.Where(path => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".vbproj" or ".fsproj")
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(IsRunnable).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }
    }

    private static bool IsRunnable(string path)
    {
        try
        {
            var root = ReadXml(path).Root;
            if (root is null || root.Descendants().Any(element => element.Name.LocalName == "IsTestProject" &&
                element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
                return false;
            return ((string?)root.Attribute("Sdk"))?.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true ||
                root.Descendants().Any(element => element.Name.LocalName == "OutputType" &&
                    element.Value.Trim().ToUpperInvariant() is "EXE" or "WINEXE");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { return false; }
    }

    public static XDocument ReadXml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_500_000
        });
        return XDocument.Load(reader);
    }

    private static string Quote(string path) => "\"" + path + "\"";

    internal static ProcessStartInfo CreateCaptureInfo(string executable, string folder, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task<(int Code, string Output, string Error)> CaptureAsync(
        ProcessStartInfo info, string label, int timeoutSeconds, CancellationToken token, Action<string>? errorLine = null)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException($"{label} başlatılamadı.");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var cancellation = KillOnCancellation(process, timeout.Token);
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = errorLine is null
                ? process.StandardError.ReadToEndAsync(timeout.Token)
                : ReadLinesAsync(process.StandardError, errorLine, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException($"{label} işlemi zaman aşımına uğradı.");
        }
    }

    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken token)
    {
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            output.AppendLine(line);
            onLine(line);
        }
        return output.ToString();
    }

    private static CancellationTokenRegistration KillOnCancellation(Process process, CancellationToken token) =>
        token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

    public static async Task<int> RunAsync(ProjectCommand command, string folder, Action<string> output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = folder, Arguments = command.Arguments ?? "", UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(info) ?? throw new IOException("Komut başlatılamadı.");
        using var cancellation = KillOnCancellation(process, token);
        async Task Pump(StreamReader reader)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                output(new string(buffer, 0, read));
        }

        await Task.WhenAll(process.WaitForExitAsync(token), Pump(process.StandardOutput), Pump(process.StandardError)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return process.ExitCode;
    }
}
