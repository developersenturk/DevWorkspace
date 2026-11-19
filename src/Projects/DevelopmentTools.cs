using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace DevWorkspace;

internal enum DevelopmentTool { Git, Node, DotNet, Flutter, GitHub, Docker, Osv, Editor }

internal sealed record ToolDefinition(DevelopmentTool Id, string Name, string Use, string Command, string? Package, Uri Guide);

internal sealed class ToolMissingException(ToolDefinition tool)
    : IOException(Loc.Format("LEFC47095556B", tool.Name));

internal static class DevelopmentTools
{
    internal static readonly IReadOnlyList<ToolDefinition> Catalog =
    [
        new(DevelopmentTool.Git, "Git", Loc.Text("L141D1EC787B7"), "git.exe", "Git.Git", new("https://git-scm.com/install/windows")),
        new(DevelopmentTool.Node, Loc.Text("LA20DC1B45F78"), Loc.Text("LADB55E6B6244"), "node.exe", "OpenJS.NodeJS.LTS", new("https://nodejs.org/en/download")),
        new(DevelopmentTool.DotNet, ".NET SDK", Loc.Text("L392454B853DA"), "dotnet.exe", "Microsoft.DotNet.SDK.10", new("https://dotnet.microsoft.com/download")),
        new(DevelopmentTool.Flutter, "Flutter SDK", Loc.Text("L060BFCC9E4C9"), "flutter.bat", null, new("https://docs.flutter.dev/install/manual")),
        new(DevelopmentTool.GitHub, "GitHub CLI", Loc.Text("L4633A6F7DE3E"), "gh.exe", "GitHub.cli", new("https://cli.github.com/")),
        new(DevelopmentTool.Docker, "Docker Desktop", Loc.Text("L7F41B5F91080"), "docker.exe", "Docker.DockerDesktop", new("https://docs.docker.com/desktop/setup/install/windows-install/")),
        new(DevelopmentTool.Osv, "OSV-Scanner", Loc.Text("L6CA8C48EC0AB"), "osv-scanner.exe", "Google.OSVScanner", new("https://google.github.io/osv-scanner/installation/")),
        new(DevelopmentTool.Editor, "Visual Studio Code", Loc.Text("L4325936B2E64"), "Code.exe", "Microsoft.VisualStudioCode", new("https://code.visualstudio.com/download"))
    ];

    internal static string ToolsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevWorkspace", "Tools");

    private sealed record ToolEnvironment(string[] Paths)
    {
        internal ConcurrentDictionary<DevelopmentTool, string?> Executables { get; } = new();
    }

    private static ToolEnvironment _environment = new(ReadPaths());

    internal static ToolDefinition Get(DevelopmentTool tool) => Catalog[(int)tool];

    internal static void RefreshPaths() => Volatile.Write(ref _environment, new(ReadPaths()));

    private static string[] ReadPaths() =>
        new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User }
            .SelectMany(target => (Environment.GetEnvironmentVariable("PATH", target) ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(path => Environment.ExpandEnvironmentVariables(path.Trim('"')))
            .Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string? Find(DevelopmentTool tool)
    {
        var environment = Volatile.Read(ref _environment);
        return environment.Executables.GetOrAdd(tool, id => FindExecutable(id, environment.Paths));
    }

    private static string? FindExecutable(DevelopmentTool tool, string[] paths)
    {
        string command = Get(tool).Command;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] relative = tool switch
        {
            DevelopmentTool.Git => [@"Git\cmd"],
            DevelopmentTool.Node => ["nodejs"],
            DevelopmentTool.DotNet => ["dotnet"],
            DevelopmentTool.GitHub => ["GitHub CLI"],
            DevelopmentTool.Docker => [@"Docker\Docker\resources\bin"],
            DevelopmentTool.Editor => ["Microsoft VS Code"],
            _ => []
        };
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(local, "Programs")];
        IEnumerable<string> defaults = roots.Where(Path.IsPathFullyQualified).SelectMany(root => relative.Select(path => Path.Combine(root, path)));
        if (tool == DevelopmentTool.Flutter)
            defaults = [Path.Combine(ToolsFolder, "flutter", "bin"), .. defaults];
        if (tool == DevelopmentTool.Osv)
            defaults = [Path.Combine(local, "Microsoft", "WinGet", "Links"), .. defaults];
        return paths.Concat(defaults).Select(directory => Path.Combine(directory, command))
            .FirstOrDefault(File.Exists);
    }

    internal static string? FindWinget() => Volatile.Read(ref _environment).Paths
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"))
        .Select(directory => Path.Combine(directory, "winget.exe")).FirstOrDefault(File.Exists);

    internal static string? FindPnpm() => Volatile.Read(ref _environment).Paths
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"))
        .SelectMany(directory => new[] { Path.Combine(directory, "pnpm.exe"), Path.Combine(directory, "pnpm.cmd") })
        .FirstOrDefault(File.Exists);

    internal static string Require(DevelopmentTool tool) => Find(tool) ?? throw new ToolMissingException(Get(tool));

    internal static DevelopmentTool? ForCommand(string command) =>
        Catalog.FirstOrDefault(tool => tool.Command.Equals(command, StringComparison.OrdinalIgnoreCase))?.Id;

    internal static void ApplyPath(ProcessStartInfo info, params string[] preferred) =>
        info.Environment["PATH"] = string.Join(';', preferred.Where(Path.IsPathFullyQualified)
            .Concat(Volatile.Read(ref _environment).Paths).Distinct(StringComparer.OrdinalIgnoreCase));

    internal static async Task<bool> IsReadyAsync(DevelopmentTool tool, CancellationToken token)
    {
        if (Find(tool) is not { } executable)
            return false;
        if (tool == DevelopmentTool.Node)
            return File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "npm.cmd"));
        if (tool != DevelopmentTool.DotNet)
            return true;

        var info = ProjectTools.CreateCaptureInfo(executable, Environment.SystemDirectory, ["--list-sdks"]);
        var (code, output, _) = await ProjectTools.CaptureAsync(info, Loc.Text("LE43DDB121A20"), 10, token);
        return code == 0 && !string.IsNullOrWhiteSpace(output);
    }

    internal static async Task InstallAsync(DevelopmentTool tool, IProgress<string> progress, CancellationToken token,
        bool acceptAgreements = true)
    {
        RefreshPaths();
        if (await IsReadyAsync(tool, token))
            return;
        var definition = Get(tool);
        if (tool == DevelopmentTool.Flutter)
        {
            Require(DevelopmentTool.Git);
            await FlutterInstaller.InstallAsync(progress, token);
        }
        else
        {
            string winget = FindWinget() ?? throw new IOException(Loc.Text("LCDCDD32D6A84"));
            var arguments = new List<string> { "install", "--id", definition.Package!, "--exact", "--source", "winget",
                "--no-upgrade", "--disable-interactivity" };
            if (acceptAgreements)
                arguments.AddRange(["--accept-source-agreements", "--accept-package-agreements"]);
            var info = ProjectTools.CreateCaptureInfo(winget, Environment.SystemDirectory, arguments);
            progress.Report(Loc.Format("L7E12A0A571FC", definition.Name));
            var (code, output, error) = await ProjectTools.CaptureAsync(info, definition.Name + " kurulumu", 1800, token);
            RefreshPaths();
            if (!await IsReadyAsync(tool, token))
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                if (code != 0)
                    throw new IOException(Loc.Format("L51E6EE6F0608", code, detail[^Math.Min(detail.Length, 700)..]));
                throw new IOException(Loc.Text("LDE806EED8580"));
            }
        }
        RefreshPaths();
    }
}
