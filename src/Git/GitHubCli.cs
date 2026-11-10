using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed class GitHubCliMissingException() : IOException("Bu işlem için GitHub CLI kurulumu gerekli.");

internal sealed class GitHubLoginRequiredException() : IOException("Bu işlem için GitHub’a giriş yapın.");

internal sealed class GitHubWorkflowPermissionException() : IOException(
    "GitHub Actions dosyalarını göndermek için GitHub hesabında ek izin gerekiyor.");

internal sealed record GitHubRepositoryOwner(string Login, bool IsOrganization)
{
    public string Label => IsOrganization ? $"{Login} (organizasyon)" : $"{Login} (kişisel hesap)";
    public string CreateEndpoint => IsOrganization ? $"orgs/{Login}/repos" : "user/repos";
    public Uri RepositoryUrl(string name) => new($"https://github.com/{Login}/{name}");
}

internal sealed record GitHubRepository(string FullName, string Visibility, bool Archived, bool? CanPush,
    string? OwnerType, string? Description, string? Language, string? DefaultBranch, DateTimeOffset? PushedAt, string[]? Topics)
{
    public string Name => FullName[(FullName.IndexOf('/') + 1)..];
    public string Owner => FullName[..FullName.IndexOf('/')];
    internal string HttpsCloneUrl => $"https://github.com/{FullName}.git";
    public string DescriptionLabel => string.IsNullOrWhiteSpace(Description) ? "Açıklama eklenmemiş" : Description;
    internal bool IsOrganization => OwnerType == "Organization";
    public string VisibilityLabel => Visibility switch
    {
        "private" => "Özel",
        "internal" => "Kuruluş içi",
        "public" => "Herkese açık",
        _ => "Bilinmiyor"
    };
    public string AccessLabel => Archived ? "Arşivlenmiş" : CanPush switch
    {
        true => "Yazabilir",
        false => "Salt okunur",
        null => "Bilinmiyor"
    };
}

internal static class GitHubCli
{
    private const string Label = "GitHub CLI";
    internal const string CloneStagingPrefix = ".devworkspace-clone-";
    internal static readonly Uri InstallGuide = new("https://github.com/cli/cli/blob/trunk/docs/install_windows.md");
    internal static readonly Uri DeviceLogin = new("https://github.com/login/device");
    private static readonly Regex DeviceCode = new(@"\bone-time code(?:[: ]+|\s*\()(?<code>[A-Z0-9]{4}-[A-Z0-9]{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Task InstallAsync(CancellationToken token) =>
        DevelopmentTools.InstallAsync(DevelopmentTool.GitHub, new Progress<string>(), token, acceptAgreements: false);

    public static Task LoginAsync(IProgress<string> progress, CancellationToken token) =>
        AuthorizeAsync(["auth", "login", "--hostname", "github.com", "--web", "--skip-ssh-key"], progress, token);

    internal static Task AuthorizeWorkflowsAsync(IProgress<string> progress, CancellationToken token) =>
        AuthorizeAsync(["auth", "refresh", "--hostname", "github.com", "--scopes", "workflow"], progress, token);

    private static async Task AuthorizeAsync(string[] args, IProgress<string> progress, CancellationToken token)
    {
        var info = CreateInfo(args);
        var (code, output, error) = await ProjectTools.CaptureAsync(info, "GitHub girişi", 900, token, line =>
        {
            var match = DeviceCode.Match(line);
            if (match.Success)
                progress.Report(match.Groups["code"].Value);
        });
        if (code != 0)
            throw new IOException($"GitHub girişi tamamlanamadı. {CommandError(code, output, error)}");
    }

    public static async Task<string> RunAsync(string[] arguments, CancellationToken token, int timeoutSeconds = 30)
    {
        var (code, output, error) = await ProjectTools.CaptureAsync(CreateInfo(arguments), Label, timeoutSeconds, token);
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

    internal static async Task VerifyWorkflowPermissionAsync(CancellationToken token)
    {
        string response = await RunAsync(["api", "--hostname", "github.com", "--include", "user", "--jq", ".login"], token);
        var scopes = Regex.Match(response, @"^X-OAuth-Scopes:[ \t]*(?<scopes>[^\r\n]*)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (scopes.Success && !scopes.Groups["scopes"].Value.Split(',', StringSplitOptions.TrimEntries)
                .Contains("workflow", StringComparer.OrdinalIgnoreCase))
            throw new GitHubWorkflowPermissionException();
    }

    private static bool IsRepositoryName(string name) =>
        Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z") &&
        !name.EndsWith("/.", StringComparison.Ordinal) && !name.EndsWith("/..", StringComparison.Ordinal);

    public static async Task<GitHubRepository[]> ReadRepositoriesAsync(CancellationToken token)
    {
        string output = await RunAsync(["api", "--hostname", "github.com", "--paginate",
            "user/repos?per_page=100&affiliation=owner,collaborator,organization_member&sort=pushed",
            "--jq", """
                .[] | {FullName: .full_name, Visibility: .visibility, Archived: .archived, CanPush: .permissions.push,
                       OwnerType: .owner.type, Description: .description, Language: .language,
                       DefaultBranch: .default_branch, PushedAt: .pushed_at, Topics: .topics} | @json
                """],
            token, timeoutSeconds: 120);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<GitHubRepository>(line))
            .OfType<GitHubRepository>()
            .Where(repository => repository.FullName is { } name && IsRepositoryName(name))
            .DistinctBy(repository => repository.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<string> ReadRepositoryReadmeAsync(GitHubRepository repository, CancellationToken token)
    {
        if (!IsRepositoryName(repository.FullName))
            throw new IOException("GitHub depo adı doğrulanamadı.");

        try
        {
            return await RunAsync(["api", "--hostname", "github.com", "-H", "Accept: application/vnd.github.raw+json",
                $"repos/{repository.FullName}/readme"], token);
        }
        catch (IOException ex) when (ex.Message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
    }

    public static async Task<string> CloneRepositoryAsync(string account, GitHubRepository repository, string parent,
        CancellationToken token)
    {
        if (!IsRepositoryName(repository.FullName))
            throw new IOException("GitHub depo adı doğrulanamadı.");
        if (!string.Equals(account, await ReadAccountAsync(token), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Etkin GitHub hesabı değişti. Depo listesini yenileyin.");

        parent = Path.GetFullPath(parent);
        string name = repository.Name.TrimEnd('.');
        if (Regex.IsMatch(name, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|\z)", RegexOptions.IgnoreCase))
            name = "repo-" + name;
        string destination = Path.Combine(parent, name);
        if (Path.Exists(destination))
            throw new IOException($"Bu konumda aynı adlı bir dosya veya klasör var. Başka bir üst klasör seçin. {destination}");

        string url = repository.HttpsCloneUrl;
        string key = $"credential.{url}.helper";
        string helper = CredentialHelper();
        string staging = Path.Combine(parent, $"{CloneStagingPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var (code, output, error) = await GitProjectOperations.RunAsync(parent,
                ["clone", "--origin", "origin", "--config", $"{key}=", "--config", $"{key}={helper}",
                    "--", url, staging], token, timeoutSeconds: 900);
            if (code != 0)
                throw new IOException($"Depo indirilemedi. {CommandError(code, output, error)}");

            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    // Git objects can be read-only on Windows.
                    foreach (var file in new DirectoryInfo(staging).EnumerateFiles("*", new EnumerationOptions
                             { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                        file.IsReadOnly = false;
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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

    private static string? FindGitHubCli() => DevelopmentTools.Find(DevelopmentTool.GitHub);

    private static string CommandError(int code, string output, string error)
    {
        string detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
        // A failed sign-in can include the one-time device code in its output.
        detail = DeviceCode.Replace(detail, "one-time code");
        return $"Kod {code}. {detail[..Math.Min(detail.Length, 900)]}";
    }
}
