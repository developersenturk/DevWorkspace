using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private enum WorkspaceCheck { Health, Packages, PackageRepair, PackageVersions, Actions }

    private CancellationTokenSource? _workspaceCheck;
    private WorkspaceCheck _activeCheck;
    private string? _checkProgress;
    private string? _checksRoot;
    private readonly Dictionary<string, HealthResult> _healthReports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<WorkspaceCheck, (string Summary, DashboardLink[] Rows)> _checkReports = [];
    private readonly HashSet<string> _packageRepairTargets = new(StringComparer.OrdinalIgnoreCase);
    private bool PackageUpdateRunning => _workspaceCheck is not null && IsPackageUpdate(_activeCheck);
    private static bool IsPackageUpdate(WorkspaceCheck check) =>
        check is WorkspaceCheck.PackageRepair or WorkspaceCheck.PackageVersions;

    private void InvalidateWorkspaceChecks()
    {
        _workspaceCheck?.Cancel();
        _dashboardScan?.Cancel();
        _healthReports.Clear();
        _checkReports.Clear();
        _packageRepairTargets.Clear();
    }

    private void UpdateWorkspaceChecks(ProjectEntry[] ahead, List<DashboardLink> tasks)
    {
        if (DashboardActionRows is null)
            return;
        if (!string.Equals(_checksRoot, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
        {
            _checksRoot = _workspace?.Root;
            _healthReports.Clear();
            _checkReports.Clear();
            _packageRepairTargets.Clear();
        }

        bool running = _workspaceCheck is not null;
        bool ready = !_busy && _projects.Count > 0;
        bool hasPackages = _projects.Any(PackageSecurity.IsSupported);
        bool hasActions = _projects.Any(project => project.Git.Remotes is { Count: > 0 });
        DashboardHealthButton.Content = CheckButton(WorkspaceCheck.Health, Loc.Text("LA63B0EB6F351"));
        DashboardPackagesButton.Content = CheckButton(WorkspaceCheck.Packages, Loc.Text("L8379E9BCE67F"));
        DashboardPackageRepairButton.Content = CheckButton(WorkspaceCheck.PackageRepair, Loc.Text("L875C0631DB2C"));
        DashboardPackageVersionsButton.Content = CheckButton(WorkspaceCheck.PackageVersions, Loc.Text("LF259510EC4E6"));
        DashboardActionsButton.Content = CheckButton(WorkspaceCheck.Actions, Loc.Text("L763DD50948E1"));
        DashboardHealthButton.IsEnabled = CheckEnabled(WorkspaceCheck.Health, ready);
        DashboardPackagesButton.IsEnabled = CheckEnabled(WorkspaceCheck.Packages, ready && hasPackages);
        DashboardPackageRepairButton.IsEnabled = CheckEnabled(WorkspaceCheck.PackageRepair, ready && _packageRepairTargets.Count > 0);
        DashboardPackageVersionsButton.IsEnabled = CheckEnabled(WorkspaceCheck.PackageVersions, ready && _packageRepairTargets.Count > 0);
        DashboardActionsButton.IsEnabled = CheckEnabled(WorkspaceCheck.Actions, ready && hasActions);

        var health = _checkReports.GetValueOrDefault(WorkspaceCheck.Health);
        var packages = _checkReports.GetValueOrDefault(WorkspaceCheck.Packages);
        var actions = _checkReports.GetValueOrDefault(WorkspaceCheck.Actions);
        DashboardHealthSummary.Text = Summary(WorkspaceCheck.Health, health.Summary, Loc.Text("L5ED3D706C1AC"));
        DashboardPackagesSummary.Text = Summary(IsPackageUpdate(_activeCheck) ? _activeCheck : WorkspaceCheck.Packages, packages.Summary, hasPackages
            ? Loc.Text("L341D339E1D03")
            : Loc.Text("LE22D1CF8CD9F"));
        DashboardActionsSummary.Text = Summary(WorkspaceCheck.Actions, actions.Summary, hasActions
            ? Loc.Text("L847CFA816980")
            : Loc.Text("L7A21CA0766CC"));
        DashboardHealthRows.ItemsSource = health.Rows;
        DashboardPackageRows.ItemsSource = packages.Rows;
        DashboardActionRows.ItemsSource = actions.Rows;
        DashboardAheadRows.ItemsSource = ahead.Select(project => new DashboardLink(project.Name,
            Loc.Format("L8C5C90056ECE", project.Branch, project.Git.Ahead, project.Git.Upstream), "git-workbench", project)).ToArray();

        if (DevelopmentTools.Find(DevelopmentTool.Git) is null)
            tasks.Add(new("Git’i kur", Loc.Text("L99F02E5CDAAA"), "tools-git"));
        if (ahead.Length > 0)
            tasks.Add(new(Loc.Text("LC171C8B8E925"), Loc.Format("LB06EC83762FB", ahead.Length), "actions-section"));
        if (health.Rows is { Length: > 0 })
            tasks.Add(new(Loc.Text("LCED9A7258414"), health.Summary!, "health-section"));
        if (packages.Rows?.Any(row => row.Navigation == "external") == true)
            tasks.Add(new(Loc.Text("L93E9F1B85B52"), packages.Summary!, "packages-section"));
        if (actions.Rows?.Any(row => row.Link is not null) == true)
            tasks.Add(new(Loc.Text("L3E550115E74A"), actions.Summary!, "actions-section"));

        string CheckButton(WorkspaceCheck check, string label) => running && check == _activeCheck ? Loc.Text("L5769A8F8AECD") : label;
        bool CheckEnabled(WorkspaceCheck check, bool available) =>
            running ? check == _activeCheck && !_workspaceCheck!.IsCancellationRequested : available;
        string Summary(WorkspaceCheck check, string? completed, string initial) =>
            (running && check == _activeCheck ? _checkProgress : completed) ?? initial;
    }

    private async void DashboardHealthClick(object sender, RoutedEventArgs e) =>
        await RunWorkspaceCheckAsync(WorkspaceCheck.Health, ScanWorkspaceHealthAsync);

    private async void DashboardPackagesClick(object sender, RoutedEventArgs e) =>
        await RunWorkspaceCheckAsync(WorkspaceCheck.Packages, (workspace, projects, token) =>
            ScanWorkspacePackagesAsync(workspace, projects, null, token));

    private async void DashboardPackageRepairClick(object sender, RoutedEventArgs e)
    {
        if (_workspaceCheck is not null)
        {
            await RunWorkspaceCheckAsync(WorkspaceCheck.PackageRepair, RepairWorkspacePackagesAsync);
            return;
        }
        if (_busy || _packageRepairTargets.Count == 0)
            return;
        string message = Loc.Format("L2988696A5CEF", _packageRepairTargets.Count) +
            Loc.Text("L798785C6F6BA") +
            Loc.Text("LC47CB29F6D6A");
        if (MessageBox.Show(this, message, Loc.Text("LB3664698ABD4"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RunWorkspaceCheckAsync(WorkspaceCheck.PackageRepair, RepairWorkspacePackagesAsync);
    }

    private async Task RepairWorkspacePackagesAsync(Workspace workspace, ProjectEntry[] projects, CancellationToken token)
    {
        var targets = new HashSet<string>(_packageRepairTargets, StringComparer.OrdinalIgnoreCase);
        _packageRepairTargets.Clear();
        _checkReports.Remove(WorkspaceCheck.Packages);
        await ScanWorkspacePackagesAsync(workspace, projects, targets, token);
    }

    private async void DashboardPackageVersionsClick(object sender, RoutedEventArgs e) =>
        await RunWorkspaceCheckAsync(WorkspaceCheck.PackageVersions, ReviewPackageVersionsAsync);

    private async Task ReviewPackageVersionsAsync(Workspace workspace, ProjectEntry[] projects, CancellationToken token)
    {
        var pending = new HashSet<string>(_packageRepairTargets, StringComparer.OrdinalIgnoreCase);
        var targets = await Task.Run(() => projects.Where(PackageSecurity.IsSupported)
            .SelectMany(project => PackageSecurity.FindLockfiles(workspace, project)
                .Where(pending.Contains).Select(path => (Path: path, Project: project)))
            .DistinctBy(target => Path.Combine(Path.GetDirectoryName(target.Path)!,
                Path.GetFileName(target.Path) == "pubspec.lock" ? "pubspec.yaml" : "package.json"), StringComparer.OrdinalIgnoreCase)
            .ToArray(), token);
        if (targets.Length == 0 || !EnsureTool(DevelopmentTool.Osv))
            return;

        var notes = new List<DashboardLink>();
        int resolved = 0;
        foreach (var (path, project) in targets)
        {
            token.ThrowIfCancellationRequested();
            SetCheckProgress(Loc.Format("LE219591B17C7", project.Name));
            try
            {
                var tool = PackageSecurityRepair.RequiredTool(path)!.Value;
                if (!await ToolSetupWindow.EnsureReadyAsync(this, tool, token))
                {
                    notes.Add(new(project.Name, Loc.Format("L0C02E6ADCD1D", DevelopmentTools.Get(tool).Name), "project", project));
                    continue;
                }
                IProgress<string> progress = new Progress<string>(message =>
                {
                    if (!token.IsCancellationRequested)
                        SetCheckProgress($"{message} · {project.Name}");
                });
                var plan = await Task.Run(() => PackageSecurityRepair.PlanVersionsAsync(path, token, progress.Report), token);
                token.ThrowIfCancellationRequested();
                if (plan is null)
                {
                    notes.Add(new(project.Name, Loc.Text("L4F8400AA00DA"), "project", project));
                    continue;
                }
                if (new PackageVersionReviewWindow(project.Name, plan) { Owner = this }.ShowDialog() != true)
                {
                    notes.Add(new(project.Name, Loc.Text("L0289960D53D3"), "project", project));
                    continue;
                }
                // Invalidate the previous report before making any on-disk changes.
                _packageRepairTargets.Clear();
                _checkReports.Remove(WorkspaceCheck.Packages);
                SetCheckProgress(Loc.Format("LBB14B4F3A644", project.Name));
                int fixedCount = await PackageSecurityRepair.ApplyVersionsAsync(plan,
                    () => SetCheckProgress(Loc.Format("L89B8260C26A9", project.Name)), token);
                resolved += fixedCount;
                notes.Add(new(project.Name, Loc.Format("L9B0BEAD51E9F", fixedCount), "project", project));
            }
            catch (Exception ex) when (IsCheckError(ex) || ex is System.Net.Http.HttpRequestException ||
                ex is OperationCanceledException && !token.IsCancellationRequested || ex is System.Text.DecoderFallbackException)
            {
                string detail = ex is OperationCanceledException ? Loc.Text("LE03F17BCF7EC") : ex.Message;
                notes.Add(new(project.Name, Loc.Format("L659A596B78B9", detail), "project", project));
            }
        }
        await ScanWorkspacePackagesAsync(workspace, projects, null, token, resolved, notes);
    }

    private async void DashboardActionsClick(object sender, RoutedEventArgs e) =>
        await RunWorkspaceCheckAsync(WorkspaceCheck.Actions, ScanWorkspaceActionsAsync);

    private async Task RunWorkspaceCheckAsync(WorkspaceCheck check, Func<Workspace, ProjectEntry[], CancellationToken, Task> scan)
    {
        if (_workspaceCheck is { } active)
        {
            if (_activeCheck == check)
            {
                _checkProgress = Loc.Text("L3EA8EA87378B");
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
        string name = check switch
        {
            WorkspaceCheck.Health => Loc.Text("L2E80E71B0660"),
            WorkspaceCheck.Packages => Loc.Text("LABA523785AA1"),
            WorkspaceCheck.PackageRepair => Loc.Text("LB3664698ABD4"),
            WorkspaceCheck.PackageVersions => Loc.Text("L0A495901FE1E"),
            _ => Loc.Text("L668CF10B256C")
        };
        var operation = OperationCenter.Start(name, workspace.Config.Name,
            IsPackageUpdate(check) ? null : () =>
            {
                if (_workspaceCheck is null && _workspace?.Root == workspace.Root)
                    _ = RunWorkspaceCheckAsync(check, scan);
            });
        operation.Cancel = cancellation.Cancel;
        SetCheckProgress(Loc.Text("LF285326C6EAA"));
        try
        {
            await scan(workspace, _projects.ToArray(), cancellation.Token);
            var report = _checkReports.GetValueOrDefault(IsPackageUpdate(check) ? WorkspaceCheck.Packages : check);
            operation.Finish(cancellation.IsCancellationRequested ? Loc.Text("L7C925A8624F1") : Loc.Text("L7C6F3F5F37BC"), report.Summary);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            operation.Finish(Loc.Text("L7C925A8624F1"));
            var report = IsPackageUpdate(check) ? WorkspaceCheck.Packages : check;
            if (!_checkReports.ContainsKey(report) && string.Equals(workspace.Root, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
                _checkReports[report] = (IsPackageUpdate(check)
                    ? Loc.Text("LBEF35BD3942E") : Loc.Text("L541D110615C2"), []);
        }
        catch (Exception ex) when (IsCheckError(ex))
        {
            operation.Finish(Loc.Text("LF0873175A881"), ex.Message);
            if (string.Equals(workspace.Root, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
                _checkReports[IsPackageUpdate(check) ? WorkspaceCheck.Packages : check] =
                    (Loc.Format("LB0E8642EE9FE", ex.Message), []);
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
            SetCheckProgress(Loc.Format("L203934CF6EB8", index + 1, projects.Length, project.Name));
            HealthResult result;
            try { result = await Task.Run(() => ProjectHealth.CheckAsync(workspace, project, token), token); }
            catch (Exception ex) when (IsCheckError(ex))
            {
                result = new([new(Loc.Text("LEDD1397983CA"), ex.Message)], 0, 0, 0);
            }

            var findings = result.Findings.ToList();
            foreach (var tool in missing)
            {
                bool needed = tool switch
                {
                    DevelopmentTool.Git => project.Repository is not null || project.Kind == "Flutter / Dart",
                    DevelopmentTool.Node => ProjectDetection.IsJavaScript(project.EntryFile),
                    DevelopmentTool.DotNet => project.EntryFile is { } file && ProjectTools.IsDotNetEntry(file),
                    DevelopmentTool.Flutter => project.Kind == "Flutter / Dart",
                    DevelopmentTool.Docker => ProjectIntegrations.HasCompose(project),
                    _ => false
                };
                if (needed)
                    findings.Add(new(Loc.Text("L9A9433501663"), Loc.Format("LBFD3DEA93FC1", DevelopmentTools.Get(tool).Name)));
            }
            result = result with { Findings = findings };
            reports[project.RelativePath] = result;
            if (findings.Count > 0 || result.Skipped + result.Unresolved > 0)
            {
                string detail = findings.FirstOrDefault()?.Description ??
                    Loc.Format("L57B2D5ACFF9E", result.Skipped, result.Unresolved);
                rows.Add(new(project.Name, Loc.Format("LE88B7E090C0F", findings.Count, detail), "health-report", project));
            }
        }
        token.ThrowIfCancellationRequested();
        _healthReports.Clear();
        foreach (var (path, result) in reports)
            _healthReports.Add(path, result);
        string summary = rows.Count == 0 ? Loc.Format("L4800D3969D5E", projects.Length) :
            Loc.Format("LC27E362B0984", projects.Length, rows.Count);
        summary += $" · {DateTime.Now:g}";
        _checkReports[WorkspaceCheck.Health] = (summary, rows.ToArray());
    }

    private async Task ScanWorkspacePackagesAsync(Workspace workspace, ProjectEntry[] projects,
        HashSet<string>? repairTargets, CancellationToken token, int? approvedResolved = null, List<DashboardLink>? versionNotes = null)
    {
        var targets = await Task.Run(() => projects.Where(PackageSecurity.IsSupported)
            .Select(project => (Project: project, Files: PackageSecurity.FindLockfiles(workspace, project))).ToArray(), token);
        var rows = targets.Where(target => target.Files.Length == 0)
            .Select(target => new DashboardLink(target.Project.Name, Loc.Text("L38378EF40ECF"),
                "project", target.Project)).ToList();
        int missing = rows.Count;
        if (versionNotes is not null)
            rows.InsertRange(0, versionNotes);
        var files = targets.SelectMany(target => target.Files.Select(path => (Path: path, target.Project)))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length > 0 && !EnsureTool(DevelopmentTool.Osv))
        {
            token.ThrowIfCancellationRequested();
            _packageRepairTargets.Clear();
            rows.Insert(0, new(Loc.Text("L97BDD722A240"), Loc.Format("LB31F0C0BFE3D", files.Length), "tools-osv"));
            _checkReports[WorkspaceCheck.Packages] = (Loc.Text("LFC60D1C3EE66"), rows.ToArray());
            return;
        }
        int completed = 0, errors = 0, empty = 0;
        int resolved = approvedResolved ?? 0;
        var remainingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolReady = new Dictionary<DevelopmentTool, bool>();
        var findings = new List<(DashboardLink Link, int Rank)>();
        for (int index = 0; index < files.Length; index++)
        {
            var (path, project) = files[index];
            string file = Path.GetFileName(path);
            SetCheckProgress(Loc.Format("LAE24776ECFF6", index + 1, files.Length, project.Name));
            try
            {
                var result = await PackageSecurity.ScanAsync(path, token);
                var tool = PackageSecurityRepair.RequiredTool(path);
                if (result.Findings.Length > 0)
                {
                    if (tool is null)
                        rows.Add(new(project.Name, Loc.Format("LFC2D0429BEA2", file), "project", project));
                    else if (repairTargets?.Contains(path) == true)
                    {
                        int before = result.Findings.Length;
                        result = await RepairPackageFileAsync(path, project, result, tool.Value, toolReady, rows, token);
                        resolved += Math.Max(0, before - result.Findings.Length);
                    }
                }
                if (!result.HasPackages)
                {
                    empty++;
                    rows.Add(new(project.Name, Loc.Format("LCB8ED31DEC82", file), "project", project));
                    continue;
                }
                completed++;
                if (result.Findings.Length == 0)
                    rows.Add(new(project.Name, Loc.Format("LFB785488351B", file), "project", project));
                else if (tool is not null)
                    remainingTargets.Add(path);
                foreach (var finding in result.Findings)
                    findings.Add((new($"{finding.Name} {finding.Version} · {project.Name}",
                        $"{finding.SeverityLabel} · {finding.Advisory} · {path}", "external", project,
                        new Uri("https://osv.dev/vulnerability/" + Uri.EscapeDataString(finding.Advisory))),
                        SeverityRank(finding.Severity)));
            }
            catch (Exception ex) when (IsCheckError(ex))
            {
                errors++;
                rows.Add(new(project.Name, Loc.Format("LDA4A513A3E80", file, ex.Message), "project", project));
            }
        }
        token.ThrowIfCancellationRequested();
        if (!string.Equals(workspace.Root, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
            return;
        _packageRepairTargets.Clear();
        _packageRepairTargets.UnionWith(remainingTargets);
        var counts = new List<string> { Loc.Format("LFA8622C5F130", completed) };
        if (repairTargets is null && approvedResolved is null) counts.Add(Loc.Format("LFD9AC6BEFE40", findings.Count));
        else
        {
            counts.Add(Loc.Format("LF3D16A16472B", resolved));
            counts.Add(Loc.Format("L3F607B1BE564", findings.Count));
        }
        if (missing > 0) counts.Add(Loc.Format("L1136D546CE94", missing));
        if (empty > 0) counts.Add(Loc.Format("LDE3D32A71656", empty));
        if (errors > 0) counts.Add(Loc.Format("L22F24DD305AE", errors));
        counts.Add($"{DateTime.Now:g}");
        _checkReports[WorkspaceCheck.Packages] = (string.Join(" · ", counts),
            findings.OrderByDescending(finding => finding.Rank).Select(finding => finding.Link).Concat(rows).ToArray());
    }

    private async Task<PackageSecurityResult> RepairPackageFileAsync(string path, ProjectEntry project,
        PackageSecurityResult before, DevelopmentTool tool, Dictionary<DevelopmentTool, bool> toolReady,
        List<DashboardLink> rows, CancellationToken token)
    {
        string file = Path.GetFileName(path);
        try
        {
            if (!toolReady.TryGetValue(tool, out bool ready))
                toolReady[tool] = ready = await ToolSetupWindow.EnsureReadyAsync(this, tool, token);
            token.ThrowIfCancellationRequested();
            if (!ready)
            {
                rows.Add(new(project.Name, $"{file} · {DevelopmentTools.Get(tool).Name} eksik", "project", project));
                return before;
            }
            SetCheckProgress(Loc.Format("L83E9534A4139", project.Name, file));
            var (result, detail) = await PackageSecurityRepair.RepairAsync(path, before,
                () => SetCheckProgress(Loc.Format("L5AC8C6AC05EE", project.Name, file)), token);
            rows.Add(new(project.Name, $"{file} · {detail}", "project", project));
            return result;
        }
        catch (Exception ex) when (IsCheckError(ex))
        {
            rows.Add(new(project.Name, Loc.Format("L7705D41DAC36", file, ex.Message), "project", project));
            return await PackageSecurity.ScanAsync(path, token);
        }
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
                rows.Add((new(project.Name, run is null ? Loc.Text("L2BFFE1E75E20") :
                    $"{run.Status} · {run.Workflow} · {run.Branch} · {run.Started?.ToLocalTime():g}", "actions-report", project,
                    run?.Failed == true ? run.Link : null), run?.Failed == true));
            }
            catch (GitHubLoginRequiredException ex)
            {
                errors++;
                rows.Add((new(Loc.Text("L9A2E42901DE2"), ex.Message, "actions-report", project), false));
                break;
            }
            catch (Exception ex) when (IsCheckError(ex))
            {
                errors++;
                rows.Add((new(project.Name, Loc.Format("LCB5BB9490FC3", ex.Message), "actions-report", project), false));
            }
        }
        token.ThrowIfCancellationRequested();
        int completed = rows.Count - errors;
        string summary = completed == 0 && errors == 0 ? Loc.Text("LEF0A28F83D7C") :
            Loc.Format("LC99C62509B4F", completed, failures) +
            (errors > 0 ? Loc.Format("LAE13D9CB13E9", errors) : "");
        _checkReports[WorkspaceCheck.Actions] = ($"{summary} · {DateTime.Now:g}",
            rows.OrderByDescending(row => row.Failed).Select(row => row.Link).ToArray());
        RememberWorkspaceActivity();
        _session.Save();
    }

    private static bool IsCheckError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidOperationException or KeyNotFoundException;

    private void OpenCheckLink(Uri address)
    {
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception ex) { StatusText.Text = ex.Message; }
    }
}
