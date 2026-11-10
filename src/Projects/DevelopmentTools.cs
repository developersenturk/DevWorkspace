using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace DevWorkspace;

internal enum DevelopmentTool { Git, Node, DotNet, Flutter, GitHub, Docker, Osv, Editor }

internal sealed record ToolDefinition(DevelopmentTool Id, string Name, string Use, string Command, string? Package, Uri Guide);

internal sealed class ToolMissingException(ToolDefinition tool)
    : IOException($"{tool.Name} bulunamadı. Geliştirme araçları ekranından kurabilirsiniz.");

internal static class DevelopmentTools
{
    internal static readonly IReadOnlyList<ToolDefinition> Catalog =
    [
        new(DevelopmentTool.Git, "Git", "Depo durumu, dallar ve Git işlemleri", "git.exe", "Git.Git", new("https://git-scm.com/install/windows")),
        new(DevelopmentTool.Node, "Node.js LTS ve npm", "JavaScript proje komutları", "node.exe", "OpenJS.NodeJS.LTS", new("https://nodejs.org/en/download")),
        new(DevelopmentTool.DotNet, ".NET SDK", ".NET proje komutları, sağlık ve NuGet taraması", "dotnet.exe", "Microsoft.DotNet.SDK.10", new("https://dotnet.microsoft.com/download")),
        new(DevelopmentTool.Flutter, "Flutter SDK", "Flutter proje komutları", "flutter.bat", null, new("https://docs.flutter.dev/install/manual")),
        new(DevelopmentTool.GitHub, "GitHub CLI", "GitHub depoları ve GitHub Actions sonuçları", "gh.exe", "GitHub.cli", new("https://cli.github.com/")),
        new(DevelopmentTool.Docker, "Docker Desktop", "Docker Compose servisleri", "docker.exe", "Docker.DockerDesktop", new("https://docs.docker.com/desktop/setup/install/windows-install/")),
        new(DevelopmentTool.Osv, "OSV-Scanner", "JavaScript ve Flutter paket güvenliği", "osv-scanner.exe", "Google.OSVScanner", new("https://google.github.io/osv-scanner/installation/")),
        new(DevelopmentTool.Editor, "Visual Studio Code", "Proje klasörünü düzenleyicide açma", "Code.exe", "Microsoft.VisualStudioCode", new("https://code.visualstudio.com/download"))
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
        var (code, output, _) = await ProjectTools.CaptureAsync(info, ".NET SDK kontrolü", 10, token);
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
            string winget = FindWinget() ?? throw new IOException("WinGet bulunamadı. Windows App Installer’ı yükleyin veya resmi kurulum sayfasını açın.");
            var arguments = new List<string> { "install", "--id", definition.Package!, "--exact", "--source", "winget",
                "--no-upgrade", "--disable-interactivity" };
            if (acceptAgreements)
                arguments.AddRange(["--accept-source-agreements", "--accept-package-agreements"]);
            var info = ProjectTools.CreateCaptureInfo(winget, Environment.SystemDirectory, arguments);
            progress.Report($"{definition.Name} WinGet üzerinden kuruluyor…");
            var (code, output, error) = await ProjectTools.CaptureAsync(info, definition.Name + " kurulumu", 1800, token);
            RefreshPaths();
            if (!await IsReadyAsync(tool, token))
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                if (code != 0)
                    throw new IOException($"Kurulum tamamlanamadı (0x{code:X8}). {detail[^Math.Min(detail.Length, 700)..]}");
                throw new IOException("Kurulum tamamlandı ancak araç doğrulanamadı. Kurulum sayfasını açabilir veya yeniden kontrol edebilirsiniz.");
            }
        }
        RefreshPaths();
    }
}
