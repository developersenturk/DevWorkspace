using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Editors;

namespace DevWorkspace;

public partial class MainWindow
{
    private const string DashboardKey = "overview";
    private const int DashboardFindingPageSize = 5;
    private sealed record DashboardLink(string Title, string Detail, string Navigation, ProjectEntry? Project = null, Uri? Link = null);
    private sealed record DashboardFinding(string Severity, string Package, string Version, string Target,
        string Detail, ProjectEntry Project, string Identity);
    private sealed record DashboardSeverityTile(string Severity, int Count);
    private DashboardFinding[] _dashboardFindings = [];
    private string? _dashboardSeverity;
    private string? _dashboardWorkspaceRoot;
    private int _dashboardFindingPage;
    private CancellationTokenSource? _dashboardScan;
    private string? _dashboardScanProgress;

    private void UpdateCentralView()
    {
        if (DashboardPanel is null || GitDiffPanel is null)
            return;

        bool diffOpen = GitDiffPanel.Visibility == Visibility.Visible;
        bool overview = SelectedNavigationKey == DashboardKey;
        DashboardPanel.Visibility = overview && !diffOpen ? Visibility.Visible : Visibility.Collapsed;
        ProjectListPanel.Visibility = !overview && !diffOpen ? Visibility.Visible : Visibility.Collapsed;
        if (overview)
            UpdateDashboard();
    }

    private void UpdateDashboard()
    {
        if (DashboardPanel is null || SelectedNavigationKey != DashboardKey)
            return;

        if (!string.Equals(_dashboardWorkspaceRoot, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
        {
            _dashboardWorkspaceRoot = _workspace?.Root;
            _dashboardSeverity = null;
            _dashboardFindingPage = 0;
            DashboardFindingQuery.EditValue = null;
        }

        DashboardSubtitle.Text = _workspace is { } workspace
            ? $"{workspace.Config.Name} · {DateTime.Today:D}" : "Çalışma alanı seçilmedi.";

        var repositories = _projects.Where(project => project.Repository is not null)
            .GroupBy(project => project.Repository, StringComparer.OrdinalIgnoreCase).ToArray();
        var dirty = repositories.Select(group => group.FirstOrDefault(project => project.Git.State == GitState.Changed))
            .OfType<ProjectEntry>().OrderByDescending(project => project.Git.Changes).ToArray();
        var ahead = repositories.Select(group => group.First()).Where(project => project.Git.Ahead > 0)
            .OrderByDescending(project => project.Git.Ahead).ToArray();
        int warnings = repositories.Count(group => group.Any(HasGitWarning));
        DashboardMetrics.ItemsSource = new DashboardLink[]
        {
            new("Tüm projeler", _projects.Count.ToString("N0"), "all"),
            new("Git depoları", repositories.Length.ToString("N0"), GitRepositoriesKey),
            new("Değişiklikli depolar", dirty.Length.ToString("N0"), "changed"),
            new("Git uyarıları", warnings.ToString("N0"), "warning")
        };
        DashboardDirty.ItemsSource = dirty.Take(8).Select(project => new DashboardLink(project.Name,
            $"{project.Branch} · {project.Git.Changes ?? 0:N0} değişen dosya", "changed", project)).ToArray();
        DashboardDirtyHint.Text = dirty.Length == 0 ? "Yerel değişiklik bulunan depo yok." :
            dirty.Length > 8 ? $"İlk 8 depo gösteriliyor · toplam {dirty.Length:N0}" : "";

        var byPath = _projects.ToLookup(project => project.RelativePath, StringComparer.OrdinalIgnoreCase);
        var recent = CurrentRecent().Select(path => byPath[path].FirstOrDefault()).OfType<ProjectEntry>().Take(6)
            .Select(project => new DashboardLink(project.Name, project.DisplayPath, "recent", project)).ToArray();
        DashboardRecent.ItemsSource = recent;
        DashboardRecentHint.Text = recent.Length == 0 ? "Henüz açılan proje yok." : "";
        var filters = (_workspace?.Config.SavedFilters ?? []).OrderBy(filter => filter.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(filter => new DashboardLink(filter.Name, _navigationNodes.GetValueOrDefault(filter.Key)?.Hint ?? filter.Query, filter.Key)).ToArray();
        DashboardFilters.ItemsSource = filters;
        DashboardFiltersHint.Text = filters.Length == 0 ? "Görünüm menüsünden geçerli filtreyi kaydedebilirsin." : "";

        var targets = DashboardNuGetTargets();
        DateTimeOffset freshSince = DateTimeOffset.UtcNow.AddDays(-7);
        var audits = targets.Select(project =>
        {
            var snapshot = _session.NuGetScans.GetValueOrDefault(project.EntryFile!);
            bool failed = _nuGetStates.GetValueOrDefault(project.EntryFile!)?.LastAttemptFailed == true;
            var findings = (snapshot?.Report.Findings ?? []).OrderByDescending(item => SeverityRank(item.Severity))
                .DistinctBy(item => (item.Package, item.Version, item.Advisory)).ToArray();
            return (Project: project, Snapshot: snapshot, Failed: failed, Findings: findings);
        }).ToArray();
        var affected = audits.Where(item => item.Findings.Length > 0)
            .OrderByDescending(item => SeverityRank(item.Findings[0].Severity)).ThenBy(item => item.Project.Name).ToArray();
        int incomplete = audits.Count(item => item.Failed || item.Snapshot?.Report.Diagnostics.Count > 0);
        int unscanned = audits.Count(item => item.Snapshot is null && !item.Failed);
        int stale = audits.Count(item => item.Snapshot is { FinishedAt: var finished } && finished < freshSince);
        int completed = audits.Count(item => item.Snapshot is not null);
        DashboardNuGetSummary.Text = _dashboardScanProgress ?? (targets.Length == 0 ? "Taranabilir .NET projesi veya çözümü yok." :
            (completed == 0
                ? incomplete > 0 ? "Tamamlanan NuGet taraması yok" : "Henüz taranmadı"
                : $"{completed:N0}/{targets.Length:N0} hedefte kayıtlı tarama · {affected.Length:N0} hedefte bulgu") +
            (incomplete > 0 ? $" · {incomplete:N0} hedefte tarama uyarısı" : "") +
            (stale > 0 ? $" · {stale:N0} tarama 7 günden eski" : ""));
        DashboardNuGetResults.Visibility = completed > 0 ? Visibility.Visible : Visibility.Collapsed;
        DashboardScanButton.Content = _dashboardScan is null ? "NuGet tara" : "Durdur";
        DashboardScanButton.IsEnabled = _dashboardScan is { IsCancellationRequested: false } ||
            _dashboardScan is null && !_busy && targets.Length > 0;

        string FindingDetail(NuGetFinding[] findings) => $"{findings[0].Severity} · {findings[0].Package} {findings[0].Version} · {findings.Length:N0} bulgu";
        _dashboardFindings = affected.SelectMany(item => item.Findings.Select(finding => new DashboardFinding(
                finding.Severity, finding.Package, finding.Version, item.Project.Name,
                $"{finding.Severity} · {finding.Package} {finding.Version} · {finding.Project} · {finding.Advisory}",
                item.Project, FindingIdentity(finding))))
            .OrderByDescending(row => SeverityRank(row.Severity)).ThenBy(row => row.Package, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        DashboardSeverityMetrics.ItemsSource = new[] { "Kritik", "Yüksek", "Orta", "Düşük" }
            .Select(severity => new DashboardSeverityTile(severity, _dashboardFindings.Count(row => row.Severity == severity))).ToArray();
        UpdateDashboardFindingTable();
        DashboardNuGetWarnings.ItemsSource = audits.Where(item => item.Failed || item.Snapshot?.Report.Diagnostics.Count > 0)
            .Select(item => new DashboardLink(item.Project.Name, "Tarama uyarısını incele", "nuget-report", item.Project)).ToArray();

        var tasks = new List<DashboardLink>();
        int urgent = affected.Count(item => SeverityRank(item.Findings[0].Severity) >= 3);
        if (urgent > 0)
            tasks.Add(new("Kritik ve yüksek NuGet bulgularını incele", $"{urgent:N0} hedef · {FindingDetail(affected[0].Findings)}",
                "nuget-report", affected[0].Project));
        else if (affected.Length > 0)
            tasks.Add(new("NuGet bulgularını incele", $"{affected.Length:N0} hedefte bilinen açık bildirildi", "nuget-section"));
        if (warnings > 0)
            tasks.Add(new("Git uyarılarını incele", $"{warnings:N0} depo okunamadı veya sahiplik uyarısı veriyor", "warning"));
        if (dirty.Length > 0)
            tasks.Add(new("Yerel değişiklikleri gözden geçir", $"{dirty.Length:N0} depoda değişiklik var", "changed"));
        if (unscanned + stale + incomplete > 0)
            tasks.Add(new("NuGet taramasını tamamla veya yenile",
                $"{unscanned:N0} taranmamış · {stale:N0} eski · {incomplete:N0} uyarılı", "nuget-section"));
        UpdateWorkspaceChecks(ahead, tasks);
        UpdateWorkspaceActivity();
        UpdateTrackedFindings();
        DashboardTasks.ItemsSource = tasks;
        DashboardTaskHint.Text = tasks.Count == 0 ? "Mevcut verilerde takip edilecek iş görünmüyor." :
            "Çalışma alanının güncel durumundan oluşturuldu.";
    }

    private void DashboardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 620;
        DashboardMetrics.Tag = DashboardSeverityMetrics.Tag = narrow ? 2 : 4;
        Grid.SetColumnSpan(DashboardPriorityPanel, narrow ? 2 : 1);
        Grid.SetColumnSpan(DashboardRecentPanel, narrow ? 2 : 1);
        Grid.SetColumn(DashboardRecentPanel, narrow ? 0 : 1);
        Grid.SetRow(DashboardRecentPanel, narrow ? 1 : 0);
        DashboardPriorityPanel.Margin = narrow ? new(0, 0, 0, 10) : new(0, 0, 6, 0);
        DashboardRecentPanel.Margin = narrow ? new(0) : new(6, 0, 0, 0);
    }

    private void DashboardSeverityClick(object sender, RoutedEventArgs e)
    {
        string? severity = (sender as Button)?.DataContext is DashboardSeverityTile tile ? tile.Severity : null;
        _dashboardSeverity = _dashboardSeverity == severity ? null : severity;
        _dashboardFindingPage = 0;
        UpdateDashboardFindingTable();
    }

    private void DashboardFindingQueryChanged(object sender, EditValueChangedEventArgs e)
    {
        _dashboardFindingPage = 0;
        UpdateDashboardFindingTable();
    }

    private void DashboardPreviousFindingsClick(object sender, RoutedEventArgs e)
    {
        _dashboardFindingPage--;
        UpdateDashboardFindingTable();
    }

    private void DashboardNextFindingsClick(object sender, RoutedEventArgs e)
    {
        _dashboardFindingPage++;
        UpdateDashboardFindingTable();
    }

    private void UpdateDashboardFindingTable()
    {
        if (DashboardFindingFooter is null)
            return;

        string query = (DashboardFindingQuery.EditValue as string)?.Trim() ?? "";
        var filtered = _dashboardFindings.Where(row =>
            (_dashboardSeverity is null || row.Severity == _dashboardSeverity) &&
            (query.Length == 0 || row.Target.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             row.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        int lastPage = Math.Max(0, (filtered.Length - 1) / DashboardFindingPageSize);
        _dashboardFindingPage = Math.Clamp(_dashboardFindingPage, 0, lastPage);
        int start = _dashboardFindingPage * DashboardFindingPageSize;
        var rows = filtered.Skip(start).Take(DashboardFindingPageSize).ToArray();
        DashboardNuGet.ItemsSource = rows;
        DashboardAllSeverities.Content = _dashboardSeverity is null ? "Tümü" : $"{_dashboardSeverity} ×";
        DashboardFindingEmpty.Text = query.Length > 0 || _dashboardSeverity is not null
            ? "Bu filtreyle eşleşen bulgu yok." : "Kayıtlı raporlarda bilinen açık bulunmuyor.";
        DashboardFindingEmpty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DashboardFindingFooter.Visibility = filtered.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DashboardFindingCount.Text = lastPage == 0 ? $"{filtered.Length:N0} bulgu" :
            $"{start + 1:N0}–{start + rows.Length:N0} / {filtered.Length:N0} bulgu";
        DashboardFindingPager.Visibility = lastPage > 0 ? Visibility.Visible : Visibility.Collapsed;
        DashboardPreviousFindings.IsEnabled = _dashboardFindingPage > 0;
        DashboardNextFindings.IsEnabled = _dashboardFindingPage < lastPage;
    }

    private void DashboardFindingClick(object sender, RoutedEventArgs e)
    {
        if (!_busy && sender is Button { DataContext: DashboardFinding finding } && FindDashboardProject(finding.Project) is { } project)
            ShowProjectTools(ProjectToolView.NuGet, project);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Kritik" => 4, "Yüksek" => 3, "Orta" => 2, "Düşük" => 1, _ => 0
    };

    private ProjectEntry[] DashboardNuGetTargets() => _projects.Where(ProjectIntegrations.HasNuGet)
        .DistinctBy(project => project.EntryFile, StringComparer.OrdinalIgnoreCase).ToArray();

    private void DashboardLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DashboardLink link } || _busy)
            return;

        var project = link.Project is { } previous ? FindDashboardProject(previous) : null;
        switch (link.Navigation)
        {
            case "nuget-report" when project is not null:
                ShowProjectTools(ProjectToolView.NuGet, project);
                break;
            case "health-report" when project is not null:
                ShowProjectTools(ProjectToolView.Health, project, _healthReports.GetValueOrDefault(project.RelativePath));
                break;
            case "actions-report" when project is not null:
                ShowProjectTools(ProjectToolView.Actions, project);
                break;
            case "git-workbench" when project is not null:
                NavigateFromDashboard(GitRepositoriesKey, project);
                OpenGitWorkbench(project);
                break;
            case "project":
                NavigateFromDashboard("all", project);
                break;
            case "tracked-findings":
                ShowTrackedFindings();
                break;
            case "external" when link.Link is not null:
                OpenCheckLink(link.Link);
                break;
            case "tools-git":
                ShowDevelopmentTools(DevelopmentTool.Git);
                break;
            case "tools-osv":
                ShowDevelopmentTools(DevelopmentTool.Osv);
                break;
            case "nuget-section":
                DashboardScanButton.BringIntoView();
                DashboardScanButton.Focus();
                break;
            case "health-section":
                DashboardHealthButton.BringIntoView();
                DashboardHealthButton.Focus();
                break;
            case "packages-section":
                DashboardPackagesButton.BringIntoView();
                DashboardPackagesButton.Focus();
                break;
            case "actions-section":
                DashboardActionsButton.BringIntoView();
                DashboardActionsButton.Focus();
                break;
            default:
                NavigateFromDashboard(link.Navigation, project);
                break;
        }
    }

    private ProjectEntry? FindDashboardProject(ProjectEntry previous) => _projects.FirstOrDefault(item =>
        item.RelativePath.Equals(previous.RelativePath, StringComparison.OrdinalIgnoreCase));

    private void NavigationLinkClick(object sender, RoutedEventArgs e)
    {
        if (!_busy && sender is Button { Tag: string key })
            NavigateFromDashboard(key);
    }

    private void NavigateFromDashboard(string key, ProjectEntry? project = null)
    {
        if (!_navigationNodes.TryGetValue(key, out var node))
            return;
        Navigation.SelectedItem = node;
        if (CurrentProjectFilter is null)
            SearchBox.Text = "";
        if (project is not null && _visibleProjects.Contains(project))
        {
            FocusProject(project);
            ShowProjectDrawer(project);
        }
    }

    private async void DashboardScanClick(object sender, RoutedEventArgs e)
    {
        if (_dashboardScan is { } running)
        {
            running.Cancel();
            _dashboardScanProgress = "NuGet taraması durduruluyor…";
            UpdateDashboard();
            return;
        }
        if (_busy || _workspace is not { } workspace || DashboardNuGetTargets() is not { Length: > 0 } targets)
            return;

        using var scan = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _dashboardScan = scan;
        try
        {
            if (!await EnsureSdkAsync())
                return;
            for (int i = 0; i < targets.Length && !scan.IsCancellationRequested; i++)
            {
                if (!string.Equals(_workspace?.Root, workspace.Root, StringComparison.OrdinalIgnoreCase))
                    break;
                var project = targets[i];
                _dashboardScanProgress = $"NuGet taranıyor · {i + 1}/{targets.Length} · {project.Name}";
                UpdateDashboard();
                if (GetNuGetState(project) is { } state)
                    await state.ScanAsync(project, scan.Token);
            }
        }
        finally
        {
            _dashboardScan = null;
            _dashboardScanProgress = null;
            if (!_closing.IsCancellationRequested)
                UpdateDashboard();
        }
    }
}
