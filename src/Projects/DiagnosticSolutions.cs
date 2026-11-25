using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed record DiagnosticSolution(string Code, string Title, string Detail, ProjectCommand? Fix = null,
    DevelopmentTool? Tool = null, int? Port = null)
{
    public string Command => Fix?.Display ?? (Tool is { } tool ? DevelopmentTools.Get(tool).Name : "—");
}
internal sealed record PortOwner(int Pid, string Process, string Address);

internal static class DiagnosticSolutions
{
    internal static DiagnosticSolution[] Analyze(string text, ProjectEntry project)
    {
        if (text.Length > 160000) text = text[^160000..];
        string lower = text.ToLowerInvariant();
        bool Has(params string[] values) => values.Any(lower.Contains);
        var result = new List<DiagnosticSolution>();
        if (Has("eaddrinuse", "address already in use", "only one usage of each socket", "port is already allocated", "portoccupied"))
        {
            var match = Regex.Match(text, @"(?i)(?:127\.0\.0\.1|0\.0\.0\.0|localhost|\[::\]|:::?)\s*:\s*(?<port>\d{1,5})|(?:port|portu)\s*[:=]?\s*(?<port>\d{1,5})", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            int? port = int.TryParse(match.Groups["port"].Value, out int value) && value is > 0 and <= 65535 ? value : null;
            result.Add(new("port", WorkbenchText.T("DiagnosticPort"), WorkbenchText.T("DiagnosticPortHelp"), Port: port));
        }
        if (Has("netsdk1045", "netsdk1147", "compatible .net sdk was not found", "you must install or update .net", "no .net sdks were found", "net sdk version specified"))
            result.Add(new("sdk", WorkbenchText.T("DiagnosticSdk"), WorkbenchText.T("DiagnosticSdkHelp"),
                new(WorkbenchText.T("CheckSdk"), "dotnet.exe", "--list-sdks"), DevelopmentTool.DotNet));
        if (Has("netsdk1004", "project.assets.json", "nu1101", "nu1301"))
            result.Add(new("restore-dotnet", WorkbenchText.T("DiagnosticRestore"), WorkbenchText.T("DiagnosticRestoreHelp"),
                project.EntryFile is { } entry && ProjectTools.IsDotNetEntry(entry)
                    ? new(WorkbenchText.T("RestorePackages"), "dotnet.exe", "restore \"" + entry + "\"") : null));
        if (Has("cannot find module", "err_module_not_found", "command not found: vite", "'vite' is not recognized", "node_modules is missing", "could not resolve dependency") &&
            File.Exists(Path.Combine(project.Folder, "package.json")))
        {
            string args = File.Exists(Path.Combine(project.Folder, "pnpm-lock.yaml")) ? "/d /c pnpm.cmd install --frozen-lockfile" :
                File.Exists(Path.Combine(project.Folder, "yarn.lock")) ? "/d /c yarn.cmd install" :
                File.Exists(Path.Combine(project.Folder, "package-lock.json")) ? "/d /c npm.cmd ci" : "/d /c npm.cmd install";
            result.Add(new("restore-node", WorkbenchText.T("DiagnosticDependencies"), WorkbenchText.T("DiagnosticDependenciesHelp"),
                new(WorkbenchText.T("RestorePackages"), "cmd.exe", args), DevelopmentTool.Node));
        }
        if (Has("modulenotfounderror", "no module named") && File.Exists(Path.Combine(project.Folder, "requirements.txt")))
            result.Add(new("restore-python", WorkbenchText.T("DiagnosticDependencies"), WorkbenchText.T("DiagnosticPythonHelp"),
                new(WorkbenchText.T("RestorePackages"), "python.exe", "-m pip install -r requirements.txt")));
        if (Has("environment variable", "missing env", ".env not found", "env file not found", "configuration key") &&
            Has("missing", "not set", "not defined", "undefined", "not found", "required"))
            result.Add(new("environment", WorkbenchText.T("DiagnosticEnvironment"), WorkbenchText.T("DiagnosticEnvironmentHelp")));
        if (Has("econnrefused", "connection refused", "network-related or instance-specific", "could not connect to server", "actively refused"))
            result.Add(new("connection", WorkbenchText.T("DiagnosticConnection"), WorkbenchText.T("DiagnosticConnectionHelp"),
                ProjectIntegrations.HasCompose(project) ? new(WorkbenchText.T("ComposeStatus"), "docker.exe", "compose ps") : null));
        return result.ToArray();
    }

    internal static async Task<PortOwner[]> PortOwnersAsync(int port, CancellationToken token)
    {
        var info = ProjectTools.CreateCaptureInfo(Path.Combine(Environment.SystemDirectory, "netstat.exe"), Environment.SystemDirectory, ["-ano", "-p", "tcp"]);
        var (code, output, error) = await ProjectTools.CaptureAsync(info, "netstat", 10, token);
        if (code != 0) throw new IOException(error);
        var owners = new List<PortOwner>();
        foreach (var entry in RuntimePorts.Parse(output).Where(p => p.Address.EndsWith(":" + port, StringComparison.Ordinal)).Distinct())
        {
            string name;
            try { using var process = Process.GetProcessById(entry.Pid); name = process.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { name = "—"; }
            owners.Add(new(entry.Pid, name, entry.Address));
        }
        return owners.ToArray();
    }
}
