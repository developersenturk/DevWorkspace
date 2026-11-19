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
            actions.Add(new(Loc.Text("LA41DB5EBB4A7"), "dotnet.exe", $"test {Quote(entry)}"));
            foreach (string runnable in FindRunnableProjects(entry).Take(8))
                actions.Add(new(Loc.Format("LA9BB9157092C", Path.GetFileNameWithoutExtension(runnable)), "dotnet.exe", $"run --project {Quote(runnable)}"));
            actions.Add(new(Loc.Text("LAF6C5426522C"), "dotnet.exe", $"build {Quote(entry)}"));
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
                        actions.Add(new(Loc.Text("LE0FDC386FE22"), "cmd.exe", "/d /c npm.cmd test"));
                    if (scripts.TryGetProperty("dev", out _))
                        actions.Add(new(Loc.Text("LCD512A6AF993"), "cmd.exe", "/d /c npm.cmd run dev"));
                    else if (scripts.TryGetProperty("start", out _))
                        actions.Add(new(Loc.Text("LDD610212B6BE"), "cmd.exe", "/d /c npm.cmd start"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (File.Exists(Path.Combine(project.Folder, "pubspec.yaml")))
        {
            actions.Add(new(Loc.Text("L4055A909606A"), "cmd.exe", "/d /c flutter.bat test"));
            actions.Add(new(Loc.Text("L9767029B8FE1"), "cmd.exe", "/d /c flutter.bat run"));
        }
        return actions;
    }

    public static bool IsDotNetEntry(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj" or ".vbproj" or ".fsproj";

    internal static IReadOnlyList<string> FindReleaseProjects(string? entry)
    {
        if (entry is null || !File.Exists(entry) || TestProjectDetection.IsTestProject(entry)) return [];
        if (Path.GetExtension(entry).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) return [Path.GetFullPath(entry)];
        if (Path.GetExtension(entry).ToLowerInvariant() is not (".sln" or ".slnx")) return [];
        return FindRunnableProjects(entry).Where(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

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
                .Where(path => !TestProjectDetection.IsTestProject(path))
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
        if (DevelopmentTools.ForCommand(executable) is { } tool)
            executable = DevelopmentTools.Require(tool);
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        DevelopmentTools.ApplyPath(info, Path.GetDirectoryName(executable) ?? Environment.SystemDirectory);
        return info;
    }

    internal static async Task<(int Code, string Output, string Error)> CaptureAsync(
        ProcessStartInfo info, string label, int timeoutSeconds, CancellationToken token, Action<string>? errorLine = null)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException(Loc.Format("L19F9ABCDFDA7", label));
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
            throw new IOException(Loc.Format("L6B4C2B49F100", label));
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

    internal static DevelopmentTool? RequiredTool(ProjectCommand command)
    {
        if (command.Executable.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase))
            return DevelopmentTool.Node;
        if (command.Executable.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (command.Arguments?.StartsWith("/d /c npm.cmd ", StringComparison.OrdinalIgnoreCase) == true)
                return DevelopmentTool.Node;
            if (command.Arguments?.StartsWith("/d /c flutter.bat ", StringComparison.OrdinalIgnoreCase) == true)
                return DevelopmentTool.Flutter;
        }
        return DevelopmentTools.ForCommand(command.Executable);
    }

    public static async Task<int> RunAsync(ProjectCommand command, string folder, Action<string> output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string executable = command.Executable;
        string arguments = command.Arguments ?? "";
        string? batch = null;
        if (RequiredTool(command) is { } tool)
        {
            executable = DevelopmentTools.Require(tool);
            bool wrapped = command.Executable.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
            if (wrapped || tool == DevelopmentTool.Flutter || command.Executable.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase))
            {
                batch = tool == DevelopmentTool.Node ? Path.Combine(Path.GetDirectoryName(executable)!, "npm.cmd") : executable;
                if (!File.Exists(batch))
                    throw new ToolMissingException(DevelopmentTools.Get(tool));
                string tail = wrapped ? arguments[(tool == DevelopmentTool.Node ? "/d /c npm.cmd ".Length : "/d /c flutter.bat ".Length)..] : arguments;
                executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                arguments = $"/d /v:off /s /c \"\"%DEVWORKSPACE_PROJECT_TOOL%\" {tail}\"";
            }
        }
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = folder, Arguments = arguments, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (batch is not null)
        {
            info.Environment["DEVWORKSPACE_PROJECT_TOOL"] = batch;
            string[] directories = DevelopmentTools.Find(DevelopmentTool.Git) is { } git
                ? [Path.GetDirectoryName(batch)!, Path.GetDirectoryName(git)!] : [Path.GetDirectoryName(batch)!];
            DevelopmentTools.ApplyPath(info, directories);
        }
        else
        {
            DevelopmentTools.ApplyPath(info);
        }
        using var process = Process.Start(info) ?? throw new IOException(Loc.Text("LC288EBA686E0"));
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
