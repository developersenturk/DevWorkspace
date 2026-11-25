using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Grid;

namespace DevWorkspace;

internal sealed record DevelopmentCheckRow(string Project, string Category, string Name, string Detail, string? Folder = null);
internal sealed record DevelopmentRepositoryRow(ProjectEntry Project, string Name, string Repository, string Branch,
    int? Changes, int? Ahead, int? Behind, string Status);

internal sealed class DevelopmentHubWindow : ThemedWindow
{
    private readonly Workspace _workspace;
    private readonly Func<ProjectEntry[]> _projects;
    private readonly Func<WorkspaceSession> _session;
    private readonly Func<LaunchProfile[], string[]?, bool> _save;
    private readonly Func<LaunchProfile, Task> _start;
    private readonly Action _stop;
    private readonly CancellationTokenSource _closing = new();
    private readonly ComboBox _profiles = new() { MinWidth = 230, DisplayMemberPath = "Name", Margin = new(0, 0, 8, 8) };
    private readonly ComboBox _project = new() { MinWidth = 230, DisplayMemberPath = "Name", Margin = new(0, 0, 8, 8) };
    private readonly TextBox _context = Output("Context");
    private readonly TextBox _plan = Output("WorkflowPlan");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
    private readonly TextBlock _freshness = WorkspaceToolsWindow.Text("NotChecked");
    private readonly TextBox _configuration = Output("TeamConfiguration");
    private readonly GridControl _checks = WorkbenchGrid.Create(WorkbenchText.T("Environment"));
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Button _check = null!, _run = null!;
    private DateTime? _lastChecked;
    private bool _working;
    private bool _stopRequested;
    private readonly GridControl _repositories = WorkbenchGrid.Create(WorkbenchText.T("ProfileRepositories"));
    private string[] _requiredTools = [];

    internal DevelopmentHubWindow(Workspace workspace, Func<ProjectEntry[]> projects, Func<WorkspaceSession> session,
        Func<LaunchProfile[], string[]?, bool> save, Func<LaunchProfile, Task> start, Action stop, Action editProfiles,
        Action<int> tools, Func<ProjectEntry, Task> open, Action<ProjectEntry> git, Action<ProjectEntry> note, Action installTools, Action? productivity = null)
    {
        _workspace = workspace; _projects = projects; _session = session; _save = save; _start = start; _stop = stop;
        _requiredTools = session().RequiredTools ?? [];
        Title = WorkbenchText.T("DevelopmentHub"); Width = 1100; Height = 780; MinWidth = 800; MinHeight = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        AutomationProperties.SetName(_profiles, WorkbenchText.T("LaunchProfile"));
        AutomationProperties.SetName(_project, WorkbenchText.T("Project"));
        var panel = WorkspaceToolsWindow.Panel();
        var heading = WorkspaceToolsWindow.Text("DevelopmentHub"); heading.FontSize = 23; heading.FontWeight = FontWeights.SemiBold;
        WorkspaceToolsWindow.AddTop(panel, heading); WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Text("HubHint"));
        _run = WorkspaceToolsWindow.Button("StartDevelopment", async () =>
        {
            if (_profiles.SelectedItem is not LaunchProfile profile || _working) return;
            _working = true; _stopRequested = false; _run.IsEnabled = _check.IsEnabled = false;
            try
            {
                await CheckAsync(profile);
                if (_stopRequested) throw new OperationCanceledException();
                _status.Text = WorkbenchText.T("StartingDevelopment");
                await _start(profile);
                _status.Text = WorkbenchText.T("DevelopmentStarted");
            }
            catch (OperationCanceledException) { if (!_closing.IsCancellationRequested) _status.Text = WorkbenchText.T("Cancelled"); }
            catch (Exception ex) when (IsExpected(ex)) { _status.Text = ex.Message; }
            finally { _working = false; if (!_closing.IsCancellationRequested) { _run.IsEnabled = _profiles.SelectedItem is LaunchProfile; _check.IsEnabled = true; } }
        });
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(WorkspaceToolsWindow.Text("LaunchProfile"), _profiles, _run,
            WorkspaceToolsWindow.Button("StopDevelopment", () => { _stopRequested = true; _stop(); _status.Text = WorkbenchText.T("Cancelled"); }),
            WorkspaceToolsWindow.Button("EditProfiles", () => { editProfiles(); ReloadProfiles(); }),
            WorkspaceToolsWindow.Button("Runtime", () => tools(2)), WorkspaceToolsWindow.Button("Productivity", () => productivity?.Invoke())));
        _plan.Height = 95; WorkspaceToolsWindow.AddTop(panel, _plan);
        var tabs = new TabControl();
        var context = WorkspaceToolsWindow.Panel();
        WorkspaceToolsWindow.AddTop(context, WorkspaceToolsWindow.Row(_project,
            WorkspaceToolsWindow.Button("OpenProject", async () => { if (SelectedProject() is { } p) await OpenAsync(p); }),
            WorkspaceToolsWindow.Button("OpenGit", () => { if (SelectedProject() is { Repository: not null } p) git(p); }),
            WorkspaceToolsWindow.Button("EditNote", () => { if (SelectedProject() is { } p) { note(p); UpdateContext(); } }),
            WorkspaceToolsWindow.Button("GitSummary", () => tools(1))));
        context.Children.Add(_context); tabs.Items.Add(new TabItem { Header = WorkbenchText.T("Context"), Content = context });
        async Task OpenAsync(ProjectEntry project)
        {
            try { await open(project); }
            catch (Exception ex) when (IsExpected(ex)) { _status.Text = ex.Message; }
        }
        var repositories = WorkspaceToolsWindow.Panel();
        WorkspaceToolsWindow.AddTop(repositories, WorkspaceToolsWindow.Text("ProfileRepositoriesHint"));
        WorkspaceToolsWindow.AddTop(repositories, WorkspaceToolsWindow.Row(
            WorkspaceToolsWindow.Button("OpenProfileProjects", async () =>
            {
                if (_profiles.SelectedItem is not LaunchProfile profile) return;
                foreach (var step in profile.Steps.DistinctBy(s => s.Project, StringComparer.OrdinalIgnoreCase))
                    if (_projects().FirstOrDefault(p => p.RelativePath.Equals(step.Project, StringComparison.OrdinalIgnoreCase)) is { } p)
                    { if (_closing.IsCancellationRequested) return; await OpenAsync(p); }
            }), WorkspaceToolsWindow.Button("OpenGit", () => { if (_repositories.CurrentItem is DevelopmentRepositoryRow row) git(row.Project); }),
            WorkspaceToolsWindow.Button("Refresh", UpdateRepositories)));
        foreach (var (key, property, width) in new[] { ("Project", "Name", 150d), ("Branch", "Branch", 130d), ("Changes", "Changes", 80d),
            ("Ahead", "Ahead", 65d), ("Behind", "Behind", 65d), ("Status", "Status", 120d), ("Repository", "Repository", 0d) })
            _repositories.Columns.Add(WorkbenchGrid.Column(WorkbenchText.T(key), property, width == 0 ? new(1, GridColumnUnitType.Star) : new(width)));
        repositories.Children.Add(_repositories); tabs.Items.Add(new TabItem { Header = WorkbenchText.T("ProfileRepositories"), Content = repositories });
        var environment = WorkspaceToolsWindow.Panel();
        _check = WorkspaceToolsWindow.Button("CheckEnvironment", async () =>
        {
            if (_working) return; _working = true; _check.IsEnabled = _run.IsEnabled = false;
            try { await CheckAsync(_profiles.SelectedItem as LaunchProfile); _status.Text = WorkbenchText.T("CheckDone"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (IsExpected(ex)) { _status.Text = ex.Message; }
            finally { _working = false; if (!_closing.IsCancellationRequested) { _run.IsEnabled = _profiles.SelectedItem is LaunchProfile; _check.IsEnabled = true; } }
        });
        WorkspaceToolsWindow.AddTop(environment, WorkspaceToolsWindow.Row(_check,
            WorkspaceToolsWindow.Button("InstallTools", installTools), WorkspaceToolsWindow.Button("CreateEnv", CreateEnv)));
        WorkspaceToolsWindow.AddTop(environment, WorkspaceToolsWindow.Text("PreflightHint"));
        foreach (var (key, property, width) in new[] { ("Project", "Project", 160d), ("Status", "Category", 130d), ("Kind", "Name", 150d), ("Detail", "Detail", 0d) })
            _checks.Columns.Add(WorkbenchGrid.Column(WorkbenchText.T(key), property, width == 0 ? new(1, GridColumnUnitType.Star) : new(width)));
        environment.Children.Add(_checks); tabs.Items.Add(new TabItem { Header = WorkbenchText.T("Environment"), Content = environment });
        var config = WorkspaceToolsWindow.Panel();
        _configuration.IsReadOnly = false; _configuration.AcceptsReturn = true; _configuration.AcceptsTab = true;
        WorkspaceToolsWindow.AddTop(config, WorkspaceToolsWindow.Text("TeamConfigurationHint"));
        WorkspaceToolsWindow.AddTop(config, WorkspaceToolsWindow.Row(
            WorkspaceToolsWindow.Button("PreviewConfiguration", PreviewConfiguration),
            WorkspaceToolsWindow.Button("ReadConfiguration", ReadConfiguration),
            WorkspaceToolsWindow.Button("SaveConfiguration", SaveConfiguration),
            WorkspaceToolsWindow.Button("ImportConfiguration", ImportConfiguration)));
        config.Children.Add(_configuration); tabs.Items.Add(new TabItem { Header = WorkbenchText.T("TeamConfiguration"), Content = config });
        foreach (TabItem item in tabs.Items)
        {
            var header = new TextBlock { Text = item.Header?.ToString() };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); item.Header = header;
        }
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        DockPanel.SetDock(_freshness, Dock.Bottom); panel.Children.Add(_freshness);
        panel.Children.Add(tabs); Content = panel;
        _profiles.SelectionChanged += (_, _) => { UpdatePlan(); UpdateRepositories(); }; _project.SelectionChanged += (_, _) => UpdateContext();
        ReloadProfiles(); ReloadProjects();
        _timer.Tick += (_, _) => { ReloadProjects(); UpdateContext(); UpdateFreshness(); UpdateRepositories(); };
        Loaded += (_, _) => _timer.Start(); Closed += (_, _) => { _timer.Stop(); _closing.Cancel(); };
    }

    private static bool IsExpected(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        Win32Exception or InvalidOperationException or ArgumentException or System.Xml.XmlException;
    private static TextBox Output(string key)
    {
        var box = new TextBox { IsReadOnly = true, FontFamily = new("Consolas"), TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 0, 8) };
        AutomationProperties.SetName(box, WorkbenchText.T(key)); return box;
    }
    private ProjectEntry? SelectedProject() => _project.SelectedItem is ProjectEntry selected ?
        _projects().FirstOrDefault(p => p.RelativePath.Equals(selected.RelativePath, StringComparison.OrdinalIgnoreCase)) : null;
    private void ReloadProjects()
    {
        var projects = _projects(); string? previous = (_project.SelectedItem as ProjectEntry)?.RelativePath ?? _session().SelectedProject;
        if ((_project.ItemsSource as ProjectEntry[])?.SequenceEqual(projects) == true) return;
        _project.ItemsSource = projects; _project.SelectedItem = projects.FirstOrDefault(p => p.RelativePath == previous) ?? projects.FirstOrDefault();
    }
    private void ReloadProfiles()
    {
        string? selected = (_profiles.SelectedItem as LaunchProfile)?.Name ?? _session().LastProfile;
        var profiles = _session().Profiles ?? []; _profiles.ItemsSource = profiles;
        _profiles.SelectedItem = profiles.FirstOrDefault(p => p.Name == selected) ?? profiles.FirstOrDefault(); UpdatePlan();
    }
    internal void SelectProfile(string name)
    {
        ReloadProfiles();
        _profiles.SelectedItem = (_session().Profiles ?? []).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    private void UpdatePlan()
    {
        _run.IsEnabled = !_working && _profiles.SelectedItem is LaunchProfile;
        _plan.Text = _profiles.SelectedItem is LaunchProfile profile ? string.Join(Environment.NewLine, profile.Steps.Select((s, i) =>
            $"{i + 1}. {s.Project} · {s.Command?.Display ?? s.Action}" +
            (s.ReadyPort is int port ? $" · TCP 127.0.0.1:{port} · {s.TimeoutSeconds}s" : s.WaitForExit ? " · " + WorkbenchText.T("WaitForExit") + $" · {s.TimeoutSeconds}s" : ""))) : WorkbenchText.T("NoProfiles");
    }
    private void UpdateRepositories()
    {
        var selected = (_repositories.CurrentItem as DevelopmentRepositoryRow)?.Repository;
        var paths = (_profiles.SelectedItem as LaunchProfile)?.Steps.Select(s => s.Project).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _repositories.ItemsSource = _projects().Where(p => p.Repository is not null && (paths is null || paths.Contains(p.RelativePath)))
            .DistinctBy(p => p.Repository, StringComparer.OrdinalIgnoreCase).Select(p => new DevelopmentRepositoryRow(p, p.Name, p.Repository!,
                p.Branch, p.Git.Changes, p.Git.Ahead, p.Git.Behind, p.Git.Label)).ToArray();
        _repositories.CurrentItem = (_repositories.ItemsSource as DevelopmentRepositoryRow[])?.FirstOrDefault(r => r.Repository == selected);
        if (_repositories.CurrentItem is null) WorkbenchGrid.SelectFirst(_repositories);
    }
    private void UpdateContext()
    {
        if (SelectedProject() is not { } project) { _context.Text = WorkbenchText.T("NoNote"); return; }
        var note = _session().ResumeNotes?.FirstOrDefault(p => p.Key.Equals(project.RelativePath, StringComparison.OrdinalIgnoreCase)).Value;
        string value = project.Name + " · " + project.Folder + Environment.NewLine + project.Branch + " · " + project.Git.Label + " · " + project.Git.Tracking +
            Environment.NewLine + WorkbenchText.T("LaunchProfile") + ": " + (_session().LastProfile ?? "—") + Environment.NewLine + Environment.NewLine +
            WorkbenchText.T("Context") + Environment.NewLine + (note?.Context ?? WorkbenchText.T("NoNote")) + Environment.NewLine + Environment.NewLine +
            WorkbenchText.T("Next") + Environment.NewLine + note?.Next + Environment.NewLine + Environment.NewLine + WorkbenchText.T("Changes") + Environment.NewLine +
            string.Join(Environment.NewLine, (project.Git.Files ?? []).Select(f => f.StatusCode + " " + f.DisplayPath));
        if (_context.Text != value) _context.Text = value;
    }
    private void UpdateFreshness() => _freshness.Text = (_lastChecked is { } time ?
        string.Format(WorkbenchText.T("LastChecked"), time.ToLocalTime().ToString("T"), (int)(DateTime.UtcNow - time).TotalSeconds) : WorkbenchText.T("NotChecked"));

    private async Task CheckAsync(LaunchProfile? profile)
    {
        _status.Text = WorkbenchText.T("Working");
        var projects = _projects();
        var rows = await Task.Run(() => DevelopmentPreflight.ReadAsync(_workspace, projects, profile, _requiredTools, _closing.Token), _closing.Token);
        _closing.Token.ThrowIfCancellationRequested(); _checks.ItemsSource = rows; _lastChecked = DateTime.UtcNow; UpdateFreshness();
        if (profile is not null && rows.Any(DevelopmentPreflight.Blocks)) throw new IOException(WorkbenchText.T("PreflightBlocked"));
    }
    private void CreateEnv()
    {
        try
        {
            string? folder = (_checks.CurrentItem as DevelopmentCheckRow)?.Folder;
            var project = _projects().FirstOrDefault(p => p.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) ?? SelectedProject();
            if (project is null) return;
            EnvironmentChecks.CreateEnvTemplate(project.Folder);
            _status.Text = WorkbenchText.T("EnvCreated"); _lastChecked = null; UpdateFreshness();
        }
        catch (Exception ex) when (IsExpected(ex)) { _status.Text = ex.Message; }
    }
    private string ConfigPath => Path.Combine(_workspace.Root, DevelopmentConfigurationFile.FileName);
    private void PreviewConfiguration()
    {
        try { _configuration.Text = DevelopmentConfigurationFile.Export(_session().Profiles ?? [], _workspace.Root, _requiredTools); _status.Text = WorkbenchText.T("ConfigurationReview"); }
        catch (Exception ex) when (IsExpected(ex)) { _status.Text = ex.Message; }
    }
    private void ReadConfiguration()
    {
        try
        {
            if (new FileInfo(ConfigPath).Length > 1024 * 1024) throw new InvalidDataException(WorkbenchText.T("InvalidWorkflow"));
            _configuration.Text = File.ReadAllText(ConfigPath); _status.Text = WorkbenchText.T("ConfigurationReview");
        }
        catch (Exception ex) when (IsExpected(ex)) { _status.Text = ex.Message; }
    }
    private void SaveConfiguration()
    {
        try { _ = DevelopmentConfigurationFile.Import(_configuration.Text, _workspace.Root); WorkspaceSharing.WriteAtomic(ConfigPath, _configuration.Text); _status.Text = WorkbenchText.T("ConfigurationSaved") + " " + ConfigPath; }
        catch (Exception ex) when (IsExpected(ex)) { _status.Text = ex.Message; }
    }
    private void ImportConfiguration()
    {
        try
        {
            var config = DevelopmentConfigurationFile.Import(_configuration.Text, _workspace.Root);
            var projects = _projects();
            foreach (var step in config.Profiles.SelectMany(p => p.Steps))
                if (!projects.Any(p => p.LinkPath is null && p.RelativePath.Equals(step.Project, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException(string.Format(WorkbenchText.T("MissingWorkflowProject"), step.Project));
            var names = config.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!_save((_session().Profiles ?? []).Where(p => !names.Contains(p.Name)).Concat(config.Profiles).ToArray(), config.RequiredTools))
                throw new IOException(WorkbenchText.T("SaveFailed"));
            _requiredTools = config.RequiredTools; ReloadProfiles(); _status.Text = WorkbenchText.T("ConfigurationImported");
        }
        catch (Exception ex) when (IsExpected(ex)) { _status.Text = ex.Message; }
    }
}
