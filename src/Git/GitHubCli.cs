using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed class GitHubCliMissingException() : IOException("Bu işlem için GitHub CLI kurulumu gerekli.");

internal sealed class GitHubLoginRequiredException() : IOException("Bu işlem için GitHub’a giriş yapın.");

internal sealed record GitHubRepositoryOwner(string Login, bool IsOrganization)
{
    public string Label => IsOrganization ? $"{Login} (organizasyon)" : $"{Login} (kişisel hesap)";
    public string CreateEndpoint => IsOrganization ? $"orgs/{Login}/repos" : "user/repos";
    public Uri RepositoryUrl(string name) => new($"https://github.com/{Login}/{name}");
}

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

    public static async Task<string> ReadAccountAsync(CancellationToken token) =>
        (await RunAsync(["api", "--hostname", "github.com", "user", "--jq", ".login"], token)).Trim();

    public static async Task<string[]> ReadOrganizationsAsync(CancellationToken token)
    {
        string output = await RunAsync(["api", "--hostname", "github.com", "--paginate",
            "user/memberships/orgs?state=active&per_page=100", "--jq", ".[].organization.login"], token);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(login => Regex.IsMatch(login, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}\z"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task CreateRepositoryAsync(string account, GitHubRepositoryOwner owner, string name,
        bool isPrivate, CancellationToken token)
    {
        if (!string.Equals(account, await ReadAccountAsync(token), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Etkin GitHub hesabı değişti. Yayımlama penceresini yeniden açın.");

        string url;
        try
        {
            url = (await RunAsync(["api", "--hostname", "github.com", "--method", "POST", owner.CreateEndpoint,
                "-f", $"name={name}", "-F", isPrivate ? "private=true" : "private=false", "--jq", ".html_url"], token)).Trim();
        }
        catch (IOException ex) when (owner.IsOrganization && ex is not (GitHubCliMissingException or GitHubLoginRequiredException))
        {
            throw new IOException($"{owner.Login} organizasyonunda depo oluşturulamadı. {ex.Message}", ex);
        }
        if (!string.Equals(url, owner.RepositoryUrl(name).AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            throw new IOException("GitHub beklenen depo adresini döndürmedi.");
    }

    internal static string CredentialHelper()
    {
        string executable = FindGitHubCli() ?? throw new GitHubCliMissingException();
        string shellPath = executable.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal);
        return $"!'{shellPath}' auth git-credential";
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
