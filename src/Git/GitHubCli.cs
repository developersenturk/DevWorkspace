using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed class GitHubCliMissingException() : IOException("GitHub Actions için GitHub CLI kurulumu gerekli.");

internal sealed class GitHubLoginRequiredException() : IOException("Actions sonuçlarını görmek için GitHub’a giriş yapın.");

internal static class GitHubCli
{
    private const string Label = "GitHub CLI";
    internal static readonly Uri InstallGuide = new("https://github.com/cli/cli/blob/trunk/docs/install_windows.md");
    internal static readonly Uri DeviceLogin = new("https://github.com/login/device");
    private static readonly Regex DeviceCode = new(@"\bone-time code(?:[: ]+|\s*\()(?<code>[A-Z0-9]{4}-[A-Z0-9]{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task InstallAsync(CancellationToken token)
    {
        if (FindGitHubCli() is not null)
            return;

        string? winget = FindOnPath("winget.exe") ??
            ExistingFile(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\winget.exe");
        if (winget is null)
            throw new IOException("Windows Paket Yöneticisi (WinGet) bulunamadı. GitHub CLI’ı kurulum kılavuzundan yükleyebilirsiniz.");

        var info = ProjectTools.CreateCaptureInfo(winget, Environment.SystemDirectory,
            ["install", "--exact", "--id", "GitHub.cli", "--source", "winget", "--disable-interactivity"]);
        var (code, output, error) = await ProjectTools.CaptureAsync(info, "GitHub CLI kurulumu", 600, token);
        // WinGet can report that the package was already present.
        if (FindGitHubCli() is not null)
            return;
        if (code != 0)
            throw new IOException($"GitHub CLI kurulamadı. {CommandError(code, output, error)}");
        throw new IOException("Kurulum tamamlandı ancak GitHub CLI bulunamadı. Kurulum kılavuzunu açabilir veya yeniden deneyebilirsiniz.");
    }

    public static async Task LoginAsync(IProgress<string> progress, CancellationToken token)
    {
        var info = CreateInfo(["auth", "login", "--hostname", "github.com", "--web", "--skip-ssh-key"]);
        var (code, output, error) = await ProjectTools.CaptureAsync(info, "GitHub girişi", 900, token, line =>
        {
            var match = DeviceCode.Match(line);
            if (match.Success)
                progress.Report(match.Groups["code"].Value);
        });
        if (code != 0)
            throw new IOException($"GitHub girişi tamamlanamadı. {CommandError(code, output, error)}");
    }

    public static async Task<string> RunAsync(string[] arguments, CancellationToken token)
    {
        var (code, output, error) = await ProjectTools.CaptureAsync(CreateInfo(arguments), Label, 30, token);
        if (code != 0)
        {
            if (code == 4 || error.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("gh auth login", StringComparison.OrdinalIgnoreCase))
                throw new GitHubLoginRequiredException();
            throw new IOException($"{Label} tamamlanamadı. {CommandError(code, output, error)}");
        }
        return output;
    }

    private static ProcessStartInfo CreateInfo(string[] arguments)
    {
        string executable = FindGitHubCli() ?? throw new GitHubCliMissingException();
        var info = ProjectTools.CreateCaptureInfo(executable, Environment.SystemDirectory, arguments);
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["NO_COLOR"] = "1";
        info.Environment.Remove("GH_FORCE_TTY");
        info.Environment.Remove("CLICOLOR_FORCE");
        return info;
    }

    private static string? FindGitHubCli() =>
        ExistingFile(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"GitHub CLI\gh.exe") ??
        ExistingFile(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"GitHub CLI\gh.exe") ??
        ExistingFile(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\GitHub CLI\gh.exe") ??
        FindOnPath("gh.exe");

    private static string? FindOnPath(string name)
    {
        // An app that's already open still has the PATH from before the installer ran.
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            string path = Environment.GetEnvironmentVariable("Path", target) ?? "";
            foreach (string entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string directory = Environment.ExpandEnvironmentVariables(entry.Trim('"'));
                if (ExistingFile(directory, name) is { } executable)
                    return executable;
            }
        }
        return null;
    }

    private static string? ExistingFile(string directory, string name)
    {
        if (!Path.IsPathFullyQualified(directory))
            return null;
        string path = Path.Combine(directory, name);
        return File.Exists(path) ? path : null;
    }

    private static string CommandError(int code, string output, string error)
    {
        string detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
        // A failed sign-in can include the one-time device code in its output.
        detail = DeviceCode.Replace(detail, "one-time code");
        return $"Kod {code}. {detail[..Math.Min(detail.Length, 900)]}";
    }
}
