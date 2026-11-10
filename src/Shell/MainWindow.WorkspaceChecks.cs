using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private enum WorkspaceCheck { Health, Packages, Actions }

    private CancellationTokenSource? _workspaceCheck;
    private WorkspaceCheck _activeCheck;
    private string? _checkProgress;
    private string? _checksRoot;
    private readonly Dictionary<string, HealthResult> _healthReports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WorkspaceCheck, (string Summary, DashboardLink[] Rows)> _checkReports = [];

    private void UpdateWorkspaceChecks(ProjectEntry[] ahead, List<DashboardLink> tasks)
    {
        if (DashboardActionRows is null)
            return;
        if (!string.Equals(_checksRoot, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
        {
            _checksRoot = _workspace?.Root;
            _healthReports.Clear();
            _checkReports.Clear();
        }

        bool running = _workspaceCheck is not null;
        bool ready = !_busy && _projects.Count > 0;
        bool hasPackages = _projects.Any(PackageSecurity.IsSupported);
        bool hasActions = _projects.Any(project => project.Git.Remotes is { Count: > 0 });
        DashboardHealthButton.Content = CheckButton(WorkspaceCheck.Health, "Projeleri kontrol et");
        DashboardPackagesButton.Content = CheckButton(WorkspaceCheck.Packages, "Paketleri tara");
        DashboardActionsButton.Content = CheckButton(WorkspaceCheck.Actions, "Actions durumunu al");
        DashboardHealthButton.IsEnabled = CheckEnabled(WorkspaceCheck.Health, ready);
        DashboardPackagesButton.IsEnabled = CheckEnabled(WorkspaceCheck.Packages, ready && hasPackages);
        DashboardActionsButton.IsEnabled = CheckEnabled(WorkspaceCheck.Actions, ready && hasActions);

        var health = _checkReports.GetValueOrDefault(WorkspaceCheck.Health);
        var packages = _checkReports.GetValueOrDefault(WorkspaceCheck.Packages);
        var actions = _checkReports.GetValueOrDefault(WorkspaceCheck.Actions);
        DashboardHealthSummary.Text = Summary(WorkspaceCheck.Health, health.Summary, "Henüz kontrol edilmedi.");
        DashboardPackagesSummary.Text = Summary(WorkspaceCheck.Packages, packages.Summary, hasPackages
            ? "npm, pnpm, Yarn, Bun ve Flutter kilit dosyalarındaki sürümler OSV ile kontrol edilir."
            : "JavaScript veya Flutter projesi bulunamadı.");
        DashboardActionsSummary.Text = Summary(WorkspaceCheck.Actions, actions.Summary, hasActions
            ? "GitHub’daki en son iş akışı sonucu isteğe bağlı olarak alınır."
            : "Uzak adresi olan bir Git deposu bulunamadı.");
        DashboardHealthRows.ItemsSource = health.Rows;
        DashboardPackageRows.ItemsSource = packages.Rows;
        DashboardActionRows.ItemsSource = actions.Rows;
        DashboardAheadRows.ItemsSource = ahead.Select(project => new DashboardLink(project.Name,
            $"{project.Branch} · {project.Git.Ahead:N0} gönderilmemiş commit · {project.Git.Upstream}", "git-workbench", project)).ToArray();

        if (DevelopmentTools.Find(DevelopmentTool.Git) is null)
            tasks.Add(new("Git’i kur", "Projeler taranabilir. Depo durumu ve Git işlemleri için Git gerekli.", "tools-git"));
        if (ahead.Length > 0)
            tasks.Add(new("Gönderilmemiş commit’leri incele", $"{ahead.Length:N0} depo", "actions-section"));
        if (health.Rows is { Length: > 0 })
            tasks.Add(new("Proje sağlığı bulgularını incele", health.Summary!, "health-section"));
        if (packages.Rows?.Any(row => row.Navigation == "external") == true)
            tasks.Add(new("JavaScript ve Flutter paket açıklarını incele", packages.Summary!, "packages-section"));
        if (actions.Rows?.Any(row => row.Link is not null) == true)
            tasks.Add(new("Başarısız GitHub Actions çalıştırmalarını incele", actions.Summary!, "actions-section"));

        string CheckButton(WorkspaceCheck check, string label) => running && check == _activeCheck ? "Durdur" : label;
        bool CheckEnabled(WorkspaceCheck check, bool available) =>
            running ? check == _activeCheck && !_workspaceCheck!.IsCancellationRequested : available;
        string Summary(WorkspaceCheck check, string? completed, string initial) =>
            (running && check == _activeCheck ? _checkProgress : completed) ?? initial;
    }

    private async void DashboardHealthClick(object sender, RoutedEventArgs e) =>
        await RunWorkspaceCheckAsync(WorkspaceCheck.Health, ScanWorkspaceHealthAsync);

    private async void DashboardPackagesClick(object sender, RoutedEventArgs e) =>
        await RunWorkspaceCheckAsync(WorkspaceCheck.Packages, ScanWorkspacePackagesAsync);

    private async void DashboardActionsClick(object sender, RoutedEventArgs e) =>
        await RunWorkspaceCheckAsync(WorkspaceCheck.Actions, ScanWorkspaceActionsAsync);

    private async Task RunWorkspaceCheckAsync(WorkspaceCheck check, Func<Workspace, ProjectEntry[], CancellationToken, Task> scan)
    {
        if (_workspaceCheck is { } active)
        {
            if (_activeCheck == check)
            {
                _checkProgress = "Durduruluyor…";
                active.Cancel();
                UpdateDashboard();
            }
            return;
        }
        if (_busy || _workspace is not { } workspace || _projects.Count == 0)
            return;
        if (check == WorkspaceCheck.Actions && (!EnsureTool(DevelopmentTool.Git) || !EnsureTool(DevelopmentTool.GitHub)))
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _workspaceCheck = cancellation;
        _activeCheck = check;
        SetCheckProgress("Kontrol hazırlanıyor…");
        try
        {
            await scan(workspace, _projects.ToArray(), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_checkReports.ContainsKey(check) && string.Equals(workspace.Root, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
                _checkReports[check] = ("Kontrol durduruldu.", []);
        }
        catch (Exception ex) when (IsCheckError(ex))
        {
            if (string.Equals(workspace.Root, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
                _checkReports[check] = ($"Kontrol tamamlanamadı. {ex.Message}", []);
        }
        finally
        {
            _workspaceCheck = null;
            _checkProgress = null;
            if (!_closing.IsCancellationRequested)
                UpdateDashboard();
        }
    }

    private void SetCheckProgress(string message)
    {
        _checkProgress = message;
        UpdateDashboard();
    }

    private async Task ScanWorkspaceHealthAsync(Workspace workspace, ProjectEntry[] projects, CancellationToken token)
    {
        var reports = new Dictionary<string, HealthResult>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<DashboardLink>();
        var missing = new HashSet<DevelopmentTool>();
        foreach (var tool in new[] { DevelopmentTool.Git, DevelopmentTool.Node, DevelopmentTool.DotNet, DevelopmentTool.Flutter, DevelopmentTool.Docker })
        {
            try
            {
                if (!await Task.Run(() => DevelopmentTools.IsReadyAsync(tool, token), token))
                    missing.Add(tool);
            }
            catch (Exception ex) when (IsCheckError(ex)) { missing.Add(tool); }
        }

        for (int index = 0; index < projects.Length; index++)
        {
            var project = projects[index];
            SetCheckProgress($"Proje sağlığı · {index + 1}/{projects.Length} · {project.Name}");
            HealthResult result;
            try { result = await Task.Run(() => ProjectHealth.CheckAsync(workspace, project, token), token); }
            catch (Exception ex) when (IsCheckError(ex))
            {
                result = new([new("Okunamadı", ex.Message)], 0, 0, 0);
            }

            var findings = result.Findings.ToList();
            foreach (var tool in missing)
            {
                bool needed = tool switch
                {
                    DevelopmentTool.Git => project.Repository is not null || project.Kind == "Flutter / Dart",
                    DevelopmentTool.Node => project.Kind == "JavaScript",
                    DevelopmentTool.DotNet => project.EntryFile is { } file && ProjectTools.IsDotNetEntry(file),
                    DevelopmentTool.Flutter => project.Kind == "Flutter / Dart",
                    DevelopmentTool.Docker => ProjectIntegrations.HasCompose(project),
                    _ => false
                };
                if (needed)
                    findings.Add(new("Araç", $"{DevelopmentTools.Get(tool).Name} gerekli. Geliştirme araçları ekranından kurabilirsiniz."));
            }
            result = result with { Findings = findings };
            reports[project.RelativePath] = result;
            if (findings.Count > 0 || result.Skipped + result.Unresolved > 0)
            {
                string detail = findings.FirstOrDefault()?.Description ??
                    $"{result.Skipped:N0} atlanan · {result.Unresolved:N0} çözümlenemeyen";
                rows.Add(new(project.Name, $"{findings.Count:N0} bulgu · {detail}", "health-report", project));
            }
        }
        token.ThrowIfCancellationRequested();
        _healthReports.Clear();
        foreach (var (path, result) in reports)
            _healthReports.Add(path, result);
        string summary = rows.Count == 0 ? $"{projects.Length:N0} proje kontrol edildi · bulgu yok" :
            $"{projects.Length:N0} proje kontrol edildi · {rows.Count:N0} proje incelenmeli";
        summary += $" · {DateTime.Now:g}";
        _checkReports[WorkspaceCheck.Health] = (summary, rows.ToArray());
    }

    private async Task ScanWorkspacePackagesAsync(Workspace workspace, ProjectEntry[] projects, CancellationToken token)
    {
        var targets = await Task.Run(() => projects.Where(PackageSecurity.IsSupported)
            .Select(project => (Project: project, Files: PackageSecurity.FindLockfiles(workspace, project))).ToArray(), token);
        var rows = targets.Where(target => target.Files.Length == 0)
            .Select(target => new DashboardLink(target.Project.Name, "Kilit dosyası eksik · güvenlik durumu bilinmiyor",
                "project", target.Project)).ToList();
        int missing = rows.Count;
        var files = targets.SelectMany(target => target.Files.Select(path => (Path: path, target.Project)))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 0 && !EnsureTool(DevelopmentTool.Osv))
        {
            token.ThrowIfCancellationRequested();
            rows.Insert(0, new("OSV-Scanner’ı kur", $"{files.Length:N0} kilit dosyası tarama bekliyor.", "tools-osv"));
            _checkReports[WorkspaceCheck.Packages] = ("OSV-Scanner eksik. Paket güvenliği kontrol edilmedi.", rows.ToArray());
            return;
        }
        int completed = 0, errors = 0, empty = 0;
        var findings = new List<(DashboardLink Link, int Rank)>();
        for (int index = 0; index < files.Length; index++)
        {
            var (path, project) = files[index];
            string file = Path.GetFileName(path);
            SetCheckProgress($"Paket güvenliği · {index + 1}/{files.Length} · {project.Name}");
            try
            {
                var result = await PackageSecurity.ScanAsync(path, token);
                if (!result.HasPackages)
                {
                    empty++;
                    rows.Add(new(project.Name, $"{file} · taranabilir paket bulunamadı", "project", project));
                    continue;
                }
                completed++;
                if (result.Findings.Length == 0)
                    rows.Add(new(project.Name, $"{file} · bilinen açık bulunmadı", "project", project));
                foreach (var finding in result.Findings)
                    findings.Add((new($"{finding.Name} {finding.Version} · {project.Name}",
                        $"{finding.Severity} · {finding.Advisory} · {path}", "external", project,
                        new Uri("https://osv.dev/vulnerability/" + Uri.EscapeDataString(finding.Advisory))),
                        SeverityRank(finding.Severity)));
            }
            catch (Exception ex) when (IsCheckError(ex))
            {
                errors++;
                rows.Add(new(project.Name, $"{file} · tarama başarısız · {ex.Message}", "project", project));
            }
        }
        token.ThrowIfCancellationRequested();
        var counts = new List<string> { $"{completed:N0} kilit dosyası tarandı", $"{findings.Count:N0} açık" };
        if (missing > 0) counts.Add($"{missing:N0} projede kilit dosyası eksik");
        if (empty > 0) counts.Add($"{empty:N0} dosyada taranabilir paket yok");
        if (errors > 0) counts.Add($"{errors:N0} tarama başarısız");
        counts.Add($"{DateTime.Now:g}");
        _checkReports[WorkspaceCheck.Packages] = (string.Join(" · ", counts),
            findings.OrderByDescending(finding => finding.Rank).Select(finding => finding.Link).Concat(rows).ToArray());
    }

    private async Task ScanWorkspaceActionsAsync(Workspace workspace, ProjectEntry[] projects, CancellationToken token)
    {
        var repositories = projects.Where(project => project.Repository is not null && project.Git.Remotes is { Count: > 0 })
            .DistinctBy(project => project.Repository, StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = new List<(DashboardLink Link, bool Failed)>();
        var checkedRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int failures = 0, errors = 0;
        for (int index = 0; index < repositories.Length; index++)
        {
            var project = repositories[index];
            SetCheckProgress($"GitHub Actions · {index + 1}/{repositories.Length} · {project.Name}");
            try
            {
                var remote = await ProjectIntegrations.FindGitHubRepositoryAsync(project, token);
                if (remote is null || !checkedRepositories.Add(remote))
                    continue;
                var run = await ProjectIntegrations.ReadLatestRunAsync(remote, includeJobs: false, token);
                if (run?.Failed == true)
                    failures++;
                rows.Add((new(project.Name, run is null ? "GitHub Actions geçmişi boş · Ayrıntıları aç" :
                    $"{run.Status} · {run.Workflow} · {run.Branch} · {run.Started?.ToLocalTime():g}", "actions-report", project,
                    run?.Failed == true ? run.Link : null), run?.Failed == true));
            }
            catch (GitHubLoginRequiredException ex)
            {
                errors++;
                rows.Add((new("GitHub girişi gerekli", ex.Message, "actions-report", project), false));
                break;
            }
            catch (Exception ex) when (IsCheckError(ex))
            {
                errors++;
                rows.Add((new(project.Name, $"Actions durumu alınamadı · {ex.Message}", "actions-report", project), false));
            }
        }
        token.ThrowIfCancellationRequested();
        int completed = rows.Count - errors;
        string summary = completed == 0 && errors == 0 ? "github.com uzak adresi olan depo bulunamadı." :
            $"{completed:N0} depo kontrol edildi · {failures:N0} son çalıştırma başarısız" +
            (errors > 0 ? $" · {errors:N0} sorgu tamamlanamadı" : "");
        _checkReports[WorkspaceCheck.Actions] = ($"{summary} · {DateTime.Now:g}",
            rows.OrderByDescending(row => row.Failed).Select(row => row.Link).ToArray());
    }

    private static bool IsCheckError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidOperationException or KeyNotFoundException;

    private void OpenCheckLink(Uri address)
    {
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception ex) { StatusText.Text = ex.Message; }
    }
}
