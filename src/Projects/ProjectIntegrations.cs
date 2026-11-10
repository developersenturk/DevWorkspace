using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed record NuGetFinding(string Package, string Version, string Severity, string Scope,
    string Project, string Framework, string Advisory);

internal sealed record NuGetReport(IReadOnlyList<NuGetFinding> Findings, IReadOnlyList<string> Diagnostics);

internal sealed record ActionsRun(string Workflow, string Title, string Branch, DateTimeOffset? Started,
    string Status, Uri? Link, long Id, bool Failed, string FailureDetails = "");

internal sealed record ComposeService(string Name, string Status);

internal static class ProjectIntegrations
{
    private sealed class NuGetRestoreRequiredException(string message) : IOException(message);

    public static bool HasNuGet(ProjectEntry project) => project.EntryFile is { } file &&
        ProjectTools.IsDotNetEntry(file) && File.Exists(file);

    public static bool HasCompose(ProjectEntry project) =>
        new[] { "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml" }
            .Any(name => File.Exists(Path.Combine(project.Folder, name)));

    public static async Task<NuGetReport> ScanNuGetAsync(ProjectEntry project, CancellationToken token,
        IProgress<string>? progress = null)
    {
        if (!HasNuGet(project))
            throw new IOException("NuGet taraması için bir .NET çözümü veya projesi gerekli.");

        string folder = project.Folder;
        string sdk = await RunAsync("dotnet.exe", ".NET SDK", folder, ["--version"], token);
        if (!int.TryParse(sdk.Trim().Split('.')[0], out int major) || major < 7)
            throw new IOException("Bu tarama için JSON raporu destekleyen .NET 7 veya daha yeni bir SDK gerekli.");

        string[] arguments = major >= 10
            ? ["package", "list", "--project", project.EntryFile!, "--no-restore"]
            : ["list", project.EntryFile!, "package"];
        try
        {
            return await AuditAsync();
        }
        catch (NuGetRestoreRequiredException)
        {
            progress?.Report("Eksik NuGet paket bilgileri geri yükleniyor…");
            await RunAsync("dotnet.exe", "NuGet paketlerini geri yükleme", folder, ["restore", project.EntryFile!], token, 300);
            progress?.Report("Geri yükleme tamamlandı. Güvenlik taraması yeniden çalışıyor…");
            return await AuditAsync();
        }

        async Task<NuGetReport> AuditAsync()
        {
            var (output, error) = await RunCaptureAsync("dotnet.exe", "NuGet taraması", folder,
                [.. arguments, "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1"], token, 120,
                nuGetAudit: true);
            return ParseNuGetReport(output, error);
        }
    }

    internal static NuGetReport ParseNuGetReport(string output, string error)
    {
        using var document = JsonDocument.Parse(output.Trim().TrimStart('\uFEFF'));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
            throw new IOException("NuGet taramasından geçerli bir proje raporu alınamadı.");

        var findings = new List<NuGetFinding>();
        var diagnostics = new List<string>();
        if (!string.IsNullOrWhiteSpace(error))
            diagnostics.Add(error.Trim());
        AddProblems(root, diagnostics);
        foreach (var project in projects.EnumerateArray())
        {
            AddProblems(project, diagnostics);
            string path = Value(project, "path");
            foreach (var framework in Items(project, "frameworks"))
            {
                AddProblems(framework, diagnostics);
                foreach (string scope in new[] { "topLevelPackages", "transitivePackages" })
                foreach (var package in Items(framework, scope))
                foreach (var vulnerability in Items(package, "vulnerabilities"))
                {
                    findings.Add(new(Value(package, "id"), Value(package, "resolvedVersion"),
                        Value(vulnerability, "severity").ToLowerInvariant() switch
                        {
                            "critical" => "Kritik", "high" => "Yüksek", "moderate" => "Orta",
                            "low" => "Düşük", _ => "Bilinmiyor"
                        }, scope == "topLevelPackages" ? "Doğrudan" : "Dolaylı",
                        Path.GetFileName(path), Value(framework, "framework"), Value(vulnerability, "advisoryurl")));
                }
            }
        }
        if (projects.GetArrayLength() == 0)
            diagnostics.Add("NuGet raporunda taranmış proje bulunamadı.");
        return new(findings.Distinct().ToArray(), diagnostics.Distinct().ToArray());
    }

    private static void AddProblems(JsonElement element, List<string> diagnostics)
    {
        foreach (var problem in Items(element, "problems"))
        {
            string message = Value(problem, "text");
            diagnostics.Add(message.Length == 0 ? problem.ToString() : message);
        }
    }

    public static async Task<ActionsRun?> GetLatestRunAsync(ProjectEntry project, CancellationToken token)
    {
        if (project.Repository is not { } repository)
            throw new IOException("Bu proje bir Git deposuna bağlı değil.");

        var remotes = project.Git.Remotes ?? [];
        string? upstream = remotes.OrderByDescending(name => name.Length).FirstOrDefault(name =>
            project.Git.Upstream?.StartsWith(name + "/", StringComparison.Ordinal) == true);
        string? githubRepository = null;
        foreach (string remote in new[] { upstream, "origin" }.OfType<string>().Concat(remotes).Distinct())
        {
            var result = await GitProjectOperations.RunAsync(repository, ["remote", "get-url", "--", remote], token);
            if (result.Code == 0 && GitHubRepository(result.Output.Trim()) is { } match)
            {
                githubRepository = match;
                break;
            }
        }

        if (githubRepository is null)
            throw new IOException("Bu Git deposunda bir github.com uzak adresi bulunamadı.");

        string output = await GitHubCli.RunAsync(
            ["run", "list", "--repo", githubRepository, "--limit", "1", "--json",
                "workflowName,displayTitle,headBranch,createdAt,status,conclusion,url,databaseId"], token);
        var run = ParseActionsRun(output);
        if (run is not { Failed: true, Id: > 0 })
            return run;

        try
        {
            string details = await GitHubCli.RunAsync(
                ["run", "view", run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--repo", githubRepository, "--json", "jobs"], token);
            return run with { FailureDetails = ParseFailedJobs(details) };
        }
        catch (Exception ex) when (ex is IOException or JsonException or Win32Exception)
        {
            return run with { FailureDetails = "Başarısız adımlar alınamadı. Ayrıntıları GitHub’da açabilirsiniz." };
        }
    }

    internal static string? GitHubRepository(string remote)
    {
        string path;
        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            path = remote["git@github.com:".Length..];
        else if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) &&
                 uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                 uri.Scheme is "https" or "http" or "ssh" or "git")
            path = uri.AbsolutePath.Trim('/');
        else
            return null;

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return Regex.IsMatch(path, @"^[a-zA-Z0-9][a-zA-Z0-9-]*/[a-zA-Z0-9_.-]+$") &&
            !path.EndsWith("/.") && !path.EndsWith("/..") ? "github.com/" + path : null;
    }

    internal static ActionsRun? ParseActionsRun(string output)
    {
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.GetArrayLength() == 0)
            return null;
        var run = document.RootElement[0];
        string status = Value(run, "status");
        string conclusion = Value(run, "conclusion");
        string label = (status == "completed" ? conclusion : status) switch
        {
            "success" => "Başarılı", "failure" => "Başarısız", "timed_out" => "Zaman aşımı",
            "cancelled" => "İptal edildi", "skipped" => "Atlandı", "in_progress" => "Çalışıyor",
            "queued" or "requested" or "pending" => "Sırada", "waiting" => "Bekliyor",
            "" or "completed" => "Tamamlandı", var other => other
        };
        Uri? link = Uri.TryCreate(Value(run, "url"), UriKind.Absolute, out var address) &&
            address.Scheme == Uri.UriSchemeHttps && address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? address : null;
        return new(Value(run, "workflowName"), Value(run, "displayTitle"), Value(run, "headBranch"),
            DateTimeOffset.TryParse(Value(run, "createdAt"), out var created) ? created : null, label, link,
            run.TryGetProperty("databaseId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long number)
                ? number : 0, IsFailed(conclusion));
    }

    private static bool IsFailed(string conclusion) => conclusion is "failure" or "timed_out" or "startup_failure";

    internal static string ParseFailedJobs(string output)
    {
        using var document = JsonDocument.Parse(output);
        var failed = Items(document.RootElement, "jobs").Where(job => IsFailed(Value(job, "conclusion")))
            .Select(job =>
            {
                var steps = Items(job, "steps").Where(step => IsFailed(Value(step, "conclusion")))
                    .Select(step => Value(step, "name")).Where(name => name.Length > 0).ToArray();
                return steps.Length == 0 ? Value(job, "name") : $"{Value(job, "name")} · {string.Join(", ", steps)}";
            }).Where(name => name.Length > 0).ToArray();
        return failed.Length == 0 ? "" : "Başarısız işler ve adımlar" + Environment.NewLine +
            string.Join(Environment.NewLine, failed.Take(5)) + (failed.Length > 5 ? $"\n+{failed.Length - 5} iş" : "");
    }

    public static async Task<IReadOnlyList<ComposeService>> ReadComposeAsync(ProjectEntry project, CancellationToken token)
    {
        EnsureCompose(project);
        string services = await RunAsync("docker.exe", "Docker Compose", project.Folder, ["compose", "config", "--services"], token);
        string containers = await RunAsync("docker.exe", "Docker Compose", project.Folder,
            ["compose", "ps", "--all", "--format", "json"], token);
        return ParseComposeServices(services, containers);
    }

    internal static IReadOnlyList<ComposeService> ParseComposeServices(string services, string containers)
    {
        var states = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        containers = containers.Trim();
        if (containers.StartsWith('['))
        {
            using var json = JsonDocument.Parse(containers);
            foreach (var item in json.RootElement.EnumerateArray())
                AddContainer(item);
        }
        else
        {
            foreach (string line in containers.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var json = JsonDocument.Parse(line);
                AddContainer(json.RootElement);
            }
        }
        return services.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new ComposeService(name, states.TryGetValue(name, out var found)
                ? string.Join(" · ", found.Distinct()) : "Başlatılmadı")).ToArray();

        void AddContainer(JsonElement item)
        {
            string service = Value(item, "Service");
            if (!states.TryGetValue(service, out var list))
                states[service] = list = [];
            list.Add((Value(item, "State"), Value(item, "Health")) switch
            {
                ("running", "unhealthy") => "Sağlıksız", ("running", "starting") => "Hazırlanıyor",
                ("running", _) => "Çalışıyor", ("restarting", _) => "Yeniden başlıyor", ("paused", _) => "Duraklatıldı",
                ("created", _) => "Oluşturuldu", ("exited" or "dead", _) => "Durdu", (var state, _) => state
            });
        }
    }

    public static Task<string> ChangeComposeAsync(ProjectEntry project, string service, bool start, CancellationToken token)
    {
        EnsureCompose(project);
        string[] arguments = start ? ["compose", "up", "-d", "--", service] : ["compose", "stop", "--", service];
        return RunAsync("docker.exe", "Docker Compose", project.Folder, arguments, token, 300);
    }

    public static async Task<string> ReadComposeLogsAsync(ProjectEntry project, string service, CancellationToken token)
    {
        EnsureCompose(project);
        var (output, error) = await RunCaptureAsync("docker.exe", "Docker Compose", project.Folder,
            ["compose", "logs", "--no-color", "--tail", "200", "--", service], token, 30);
        return string.Join(Environment.NewLine, new[] { output.TrimEnd(), error.TrimEnd() }.Where(text => text.Length > 0));
    }

    private static void EnsureCompose(ProjectEntry project)
    {
        if (!HasCompose(project))
            throw new IOException("Bu proje klasöründe Docker Compose dosyası bulunamadı.");
    }

    private static async Task<string> RunAsync(string executable, string label, string folder, string[] arguments,
        CancellationToken token, int seconds = 30) =>
        (await RunCaptureAsync(executable, label, folder, arguments, token, seconds)).Output;

    private static async Task<(string Output, string Error)> RunCaptureAsync(string executable, string label,
        string folder, string[] arguments, CancellationToken token, int seconds, bool nuGetAudit = false)
    {
        var info = ProjectTools.CreateCaptureInfo(executable, folder, arguments);
        if (executable == "dotnet.exe")
            info.Environment["DOTNET_NOLOGO"] = "1";
        if (nuGetAudit)
            info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        try
        {
            var (code, output, error) = await ProjectTools.CaptureAsync(info, label, seconds, token);
            if (code != 0)
            {
                string rawDetail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                string detail = nuGetAudit ? NuGetFailureDetail(output) ?? rawDetail : rawDetail;
                string message = $"{label} tamamlanamadı (kod {code}). {detail[..Math.Min(detail.Length, 1800)]}";
                if (nuGetAudit && (NeedsNuGetRestore(detail) || NeedsNuGetRestore(error)))
                    throw new NuGetRestoreRequiredException("NuGet taraması için gerekli paket bilgileri bulunamadı. " + detail);
                throw new IOException(message);
            }
            return (output, error);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 && Directory.Exists(folder))
        {
            throw new IOException($"{label} bulunamadı. Aracın kurulu ve PATH üzerinde olduğundan emin olun.", ex);
        }
    }

    private static string? NuGetFailureDetail(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output.Trim().TrimStart('\uFEFF'));
            var errors = Items(document.RootElement, "problems")
                .Where(problem => Value(problem, "level").Equals("error", StringComparison.OrdinalIgnoreCase))
                .Select(problem => Value(problem, "text")).Where(message => message.Length > 0).Distinct().ToArray();
            return errors.Length == 0 ? null : string.Join(Environment.NewLine, errors);
        }
        catch (JsonException) { return null; }
    }

    private static bool NeedsNuGetRestore(string diagnostic) =>
        diagnostic.Contains("No assets file was found", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(diagnostic, @"\bNETSDK(?:1004|1005|1047|1064)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<JsonElement> Items(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var item) &&
        item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : [];

    private static string Value(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
}
