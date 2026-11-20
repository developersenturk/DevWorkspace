using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Grid;
using Microsoft.Win32;

namespace DevWorkspace;

internal sealed class WorkspaceToolsWindow : ThemedWindow
{
    private readonly Workspace _workspace;
    private readonly ProjectEntry[] _projects;
    private readonly CancellationTokenSource _closing = new();
    private readonly TabControl _tabs = new();
    private readonly Func<ProjectTemplate[]> _templates;
    private readonly Func<ProjectTemplate[], bool> _saveTemplates;
    private readonly CancellationToken _workbenchToken;
    private readonly Action<string> _created;
    private readonly Action<ProjectEntry> _openGit;
    private readonly DispatcherTimer _runtimeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _searching;
    private bool _portsBusy;

    internal WorkspaceToolsWindow(Workspace workspace, ProjectEntry[] projects, int tab, Func<ProjectTemplate[]> templates,
        Func<ProjectTemplate[], bool> saveTemplates, CancellationToken workbenchToken, Action<string> created, Action<ProjectEntry> openGit)
    {
        _workspace = workspace; _projects = projects; _templates = templates; _saveTemplates = saveTemplates;
        _workbenchToken = workbenchToken; _created = created; _openGit = openGit;
        Title = WorkbenchText.T("Tools"); Width = 1120; Height = 740; MinWidth = 780; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        _tabs.Margin = new(12);
        AddTab("Search", SearchPanel()); AddTab("GitSummary", GitPanel()); AddTab("Runtime", RuntimePanel());
        AddTab("Dependencies", DependenciesPanel()); AddTab("Templates", TemplatesPanel()); AddTab("Environment", EnvironmentPanel());
        Content = _tabs; SelectTab(tab);
        Closed += (_, _) => { _runtimeTimer.Stop(); _searching?.Cancel(); _closing.Cancel(); };
        _tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, _tabs) && _tabs.SelectedIndex == 2) _runtimeTimer.Start(); else if (ReferenceEquals(e.Source, _tabs)) _runtimeTimer.Stop(); };
        if (tab == 2) _runtimeTimer.Start();
    }

    internal void SelectTab(int index) => _tabs.SelectedIndex = index;
    private void AddTab(string key, UIElement content) => _tabs.Items.Add(new TabItem { Header = WorkbenchText.T(key), Content = content });
    internal static TextBlock Text(string key) => new() { Text = WorkbenchText.T(key), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
    private static TextBlock StatusLine()
    {
        var style = new Style(typeof(TextBlock));
        var empty = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        style.Triggers.Add(empty);
        return new TextBlock { Style = style, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
    }
    internal static Button Button(string key, Action action)
    {
        var button = new Button { Content = WorkbenchText.T(key), Padding = new(10, 5, 10, 5), Margin = new(0, 0, 8, 8), MinHeight = 30 };
        button.Click += (_, _) => action(); return button;
    }
    internal static WrapPanel Row(params UIElement[] items)
    {
        var row = new WrapPanel(); foreach (var item in items) row.Children.Add(item); return row;
    }
    internal static DockPanel Panel() => new() { Margin = new(12), LastChildFill = true };
    internal static void AddTop(DockPanel panel, UIElement item) { DockPanel.SetDock(item, Dock.Top); panel.Children.Add(item); }
    private static GridControl Table(params (string Key, string Property, double Width)[] columns)
    {
        var grid = WorkbenchGrid.Create(string.Join(" · ", columns.Select(c => WorkbenchText.T(c.Key))));
        foreach (var (key, property, width) in columns)
            grid.Columns.Add(WorkbenchGrid.Column(WorkbenchText.T(key), property, width == 0 ? new(1, GridColumnUnitType.Star) : new(width)));
        return grid;
    }
    internal static TextBox Input(string key, double width = 220)
    {
        var input = new TextBox { MinWidth = width, Margin = new(0, 0, 8, 8), VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 30 };
        AutomationProperties.SetName(input, WorkbenchText.T(key)); return input;
    }
    private ComboBox ProjectPicker(bool all = false)
    {
        var picker = new ComboBox { MinWidth = 230, Margin = new(0, 0, 8, 8), DisplayMemberPath = "Name" };
        var options = new List<ProjectEntry>();
        if (all) options.Add(new(WorkbenchText.T("AllProjects"), "", "", "", null, "", null, new(GitState.None)));
        options.AddRange(_projects); picker.ItemsSource = options; picker.SelectedIndex = 0;
        AutomationProperties.SetName(picker, WorkbenchText.T("Project")); return picker;
    }
    private async Task Run(Button button, TextBlock status, Func<Task> action)
    {
        if (!button.IsEnabled) return;
        button.IsEnabled = false; status.Text = WorkbenchText.T("Working");
        try { await action(); }
        catch (OperationCanceledException) { if (!_closing.IsCancellationRequested) status.Text = WorkbenchText.T("Cancelled"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException or ArgumentException or InvalidOperationException or System.Xml.XmlException) { if (!_closing.IsCancellationRequested) status.Text = ex.Message; }
        finally { if (!_closing.IsCancellationRequested) button.IsEnabled = true; }
    }

    private UIElement SearchPanel()
    {
        var panel = Panel(); var query = Input("Query", 340); var scope = ProjectPicker(true); var status = Text("SearchReady");
        var matchCase = new CheckBox { Content = WorkbenchText.T("MatchCase"), Margin = new(0, 4, 8, 8) };
        var grid = Table(("Project", "Project", 150), ("File", "File", 0), ("Line", "Line", 65), ("Preview", "Preview", 360));
        Button search = null!;
        async void Find()
        {
            if (string.IsNullOrWhiteSpace(query.Text)) { status.Text = WorkbenchText.T("QueryRequired"); return; }
            string value = query.Text; bool sensitive = matchCase.IsChecked == true;
            var projects = scope.SelectedIndex == 0 ? _projects : new[] { (ProjectEntry)scope.SelectedItem };
            await Run(search, status, async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); _searching = cancellation;
                try
                {
                    var report = await Task.Run(() => WorkspaceSearch.Find(projects, _workspace.Config.ScanExclusions, value, sensitive, cancellation.Token), cancellation.Token);
                    if (_closing.IsCancellationRequested) return;
                    grid.ItemsSource = report.Hits;
                    status.Text = string.Format(WorkbenchText.T("SearchResult"), report.Hits.Length, report.Checked, report.Skipped) + (report.Limited ? " · " + WorkbenchText.T("Limited") : "");
                }
                finally { _searching = null; }
            });
        }
        search = Button("Find", Find);
        query.KeyDown += (_, e) => { if (e.Key == Key.Enter && search.IsEnabled) Find(); };
        void Open()
        {
            if (grid.CurrentItem is not SearchHit hit || !File.Exists(hit.File)) return;
            try
            {
                if (DevelopmentTools.Find(DevelopmentTool.Editor) is { } editor)
                {
                    var info = new ProcessStartInfo(editor) { UseShellExecute = true }; info.ArgumentList.Add("--goto"); info.ArgumentList.Add(hit.File + ":" + hit.Line); Process.Start(info);
                }
                else { var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")); info.ArgumentList.Add(hit.File); Process.Start(info); }
            }
            catch (Win32Exception ex) { status.Text = ex.Message; }
        }
        ((TableView)grid.View).RowDoubleClick += (_, _) => Open();
        AddTop(panel, Text("SearchHint")); AddTop(panel, Row(Text("Query"), query, scope, matchCase, search, Button("Cancel", () => _searching?.Cancel()), Button("OpenFile", Open)));
        AddTop(panel, status); panel.Children.Add(grid); return panel;
    }

    private UIElement GitPanel()
    {
        var panel = Panel(); var status = StatusLine();
        var grid = Table(("Project", "Project", 150), ("Branch", "Branch", 125), ("Upstream", "Upstream", 150), ("Ahead", "Ahead", 85), ("Behind", "Behind", 85), ("Changes", "Changes", 85), ("Stashes", "Stashes", 75), ("Detail", "Detail", 0));
        Button refresh = null!;
        refresh = Button("Refresh", async () => await Run(refresh, status, async () =>
        {
            var rows = await Task.Run(() => WorkspaceGitSummary.ReadAsync(_projects, _closing.Token), _closing.Token);
            if (_closing.IsCancellationRequested) return; grid.ItemsSource = rows;
            status.Text = string.Format(WorkbenchText.T("GitResult"), rows.Length, rows.Sum(r => r.Ahead ?? 0), rows.Sum(r => r.Behind ?? 0), rows.Sum(r => r.Stashes ?? 0));
        }));
        void Open() { if (grid.CurrentItem is RepositorySummary row && _projects.FirstOrDefault(p => p.Repository == row.Repository) is { } project) _openGit(project); }
        AddTop(panel, Row(refresh, Button("OpenGit", Open))); AddTop(panel, Text("GitHint")); AddTop(panel, status); panel.Children.Add(grid);
        return panel;
    }

    private UIElement RuntimePanel()
    {
        var panel = Panel(); var status = Text("RuntimeHint");
        var grid = Table(("Project", "Target", 150), ("Command", "Name", 180), ("Pid", "ProcessId", 80), ("Ports", "Ports", 0), ("Status", "Status", 130));
        var output = new TextBox { IsReadOnly = true, FontFamily = new("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(output, WorkbenchText.T("Logs"));
        var stop = Button("Stop", () => (grid.CurrentItem as WorkspaceOperation)?.Cancel?.Invoke());
        var restart = Button("Restart", () =>
        {
            if (grid.CurrentItem is WorkspaceOperation { IsRunning: false, Command: { } command, Folder: { } folder } operation)
                _ = OperationCenter.RunCommandAsync(command, operation.Target, folder, _workbenchToken);
        });
        void Update()
        {
            var selected = grid.CurrentItem as WorkspaceOperation;
            var rows = OperationCenter.Entries.Where(e => e.Command is not null).ToArray();
            grid.ItemsSource = rows; if (selected is not null && rows.Contains(selected)) grid.CurrentItem = selected;
            else WorkbenchGrid.SelectFirst(grid);
            var current = grid.CurrentItem as WorkspaceOperation;
            string text = current?.ReadOutput() ?? ""; if (output.Text != text) output.Text = text;
            stop.IsEnabled = current is { IsRunning: true, Cancel: not null };
            restart.IsEnabled = current is { IsRunning: false, Command: not null };
        }
        async void Ports()
        {
            if (_portsBusy || _closing.IsCancellationRequested) return;
            _portsBusy = true;
            try
            {
                var operations = OperationCenter.Entries.Where(e => e.Command is not null).ToArray();
                await Task.Run(() => RuntimePorts.RefreshAsync(operations, _closing.Token), _closing.Token);
                if (!_closing.IsCancellationRequested) { status.Text = WorkbenchText.T("RuntimeHint"); Update(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException) { if (!_closing.IsCancellationRequested) status.Text = ex.Message; }
            finally { _portsBusy = false; }
        }
        _runtimeTimer.Tick += (_, _) => { Update(); Ports(); };
        grid.CurrentItemChanged += (_, _) =>
        {
            var current = grid.CurrentItem as WorkspaceOperation;
            output.Text = current?.ReadOutput() ?? "";
            stop.IsEnabled = current is { IsRunning: true, Cancel: not null };
            restart.IsEnabled = current is { IsRunning: false, Command: not null };
        };
        AddTop(panel, Row(stop, restart, Button("Refresh", Ports))); AddTop(panel, status);
        var split = new Grid(); split.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); split.RowDefinitions.Add(new() { Height = new(6) }); split.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        split.Children.Add(grid); Grid.SetRow(output, 2); split.Children.Add(output);
        var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(splitter, 1); split.Children.Add(splitter);
        panel.Children.Add(split); Loaded += (_, _) => { Update(); if (_tabs.SelectedIndex == 2) Ports(); }; return panel;
    }

    private UIElement DependenciesPanel()
    {
        var panel = Panel(); var status = StatusLine();
        var nodes = new ComboBox { MinWidth = 350, Margin = new(0, 0, 8, 8) }; AutomationProperties.SetName(nodes, WorkbenchText.T("Project"));
        var tree = new TreeView(); var edges = Table(("Source", "Source", 0), ("Target", "Target", 0), ("Kind", "Kind", 100));
        DependencyReport? report = null;
        void ShowNode()
        {
            tree.Items.Clear(); if (report is null || nodes.SelectedItem is not string selected) return;
            foreach (bool reverse in new[] { false, true })
            {
                var group = new TreeViewItem { Header = WorkbenchText.T(reverse ? "Affected" : "Requires"), IsExpanded = true };
                foreach (string node in report.Reachable(selected, reverse)) group.Items.Add(new TreeViewItem { Header = node, ToolTip = node });
                if (group.Items.Count == 0) group.Items.Add(WorkbenchText.T("None")); tree.Items.Add(group);
            }
        }
        nodes.SelectionChanged += (_, _) => ShowNode();
        Button scan = null!;
        scan = Button("Scan", async () => await Run(scan, status, async () =>
        {
            report = await Task.Run(() => WorkspaceDependencies.ReadAsync(_projects, _workspace.Config.ScanExclusions, _closing.Token), _closing.Token);
            if (_closing.IsCancellationRequested) return;
            edges.ItemsSource = report.Edges; nodes.ItemsSource = report.Edges.SelectMany(e => new[] { e.Source, e.Target }).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(); nodes.SelectedIndex = 0;
            status.Text = string.Format(WorkbenchText.T("DependencyResult"), report.Edges.Length, report.Diagnostics.Length);
            status.ToolTip = string.Join(Environment.NewLine, report.Diagnostics); ShowNode();
        }));
        AddTop(panel, Row(scan, nodes)); AddTop(panel, Text("DependenciesHint")); AddTop(panel, status);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = WorkbenchText.T("Impact"), Content = tree }); tabs.Items.Add(new TabItem { Header = WorkbenchText.T("Connections"), Content = edges }); panel.Children.Add(tabs); return panel;
    }

    private UIElement EnvironmentPanel()
    {
        var panel = Panel(); var picker = ProjectPicker(); var status = Text("EnvironmentHint");
        var table = Table(("Status", "Category", 150), ("Name", "Name", 180), ("Detail", "Detail", 0));
        Button scan = null!; scan = Button("Check", async () =>
        {
            if (picker.SelectedItem is not ProjectEntry project) return;
            await Run(scan, status, async () =>
            {
                var rows = await Task.Run(() => ProjectInspection.EnvironmentAsync(_workspace, project, _closing.Token), _closing.Token);
                if (_closing.IsCancellationRequested) return;
                table.ItemsSource = rows; status.Text = WorkbenchText.T("CheckDone");
            });
        });
        AddTop(panel, Row(picker, scan)); AddTop(panel, status); panel.Children.Add(table); return panel;
    }

    private UIElement TemplatesPanel()
    {
        var panel = Panel(); var templates = new ComboBox { MinWidth = 230, DisplayMemberPath = "Name", Margin = new(0, 0, 8, 8) }; AutomationProperties.SetName(templates, WorkbenchText.T("Templates"));
        var name = Input("ProjectName"); var parent = Input("Destination", 350); parent.Text = _workspace.Root;
        var initGit = new CheckBox { Content = WorkbenchText.T("InitGit"), Margin = new(0, 5, 8, 8) };
        var status = StatusLine(); var preview = new TextBox { IsReadOnly = true, FontFamily = new("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(preview, WorkbenchText.T("Preview"));
        TemplateFile[]? plan = null; string? planName = null, planParent = null;
        void Reload() { templates.ItemsSource = ProjectTemplates.BuiltIns.Concat(_templates()).ToArray(); templates.SelectedIndex = 0; }
        Button create = null!; Button inspect = null!;
        void Invalidate() { plan = null; if (create is not null) create.IsEnabled = false; preview.Clear(); }
        name.TextChanged += (_, _) => Invalidate(); parent.TextChanged += (_, _) => Invalidate(); templates.SelectionChanged += (_, _) => Invalidate();
        var register = Button("RegisterTemplate", () =>
        {
            var picker = new OpenFolderDialog { Title = WorkbenchText.T("RegisterTemplate") };
            if (picker.ShowDialog(this) != true) return;
            var saved = _templates().Where(t => !string.Equals(t.Folder, picker.FolderName, StringComparison.OrdinalIgnoreCase)).Append(new ProjectTemplate(Path.GetFileName(picker.FolderName), picker.FolderName)).ToArray();
            if (!_saveTemplates(saved)) { status.Text = WorkbenchText.T("SaveFailed"); return; }
            Reload(); templates.SelectedItem = saved[^1];
        });
        var remove = Button("RemoveTemplate", () =>
        {
            if (templates.SelectedItem is not ProjectTemplate { Folder: not null } template) return;
            if (!_saveTemplates(_templates().Where(t => t != template).ToArray())) { status.Text = WorkbenchText.T("SaveFailed"); return; } Reload();
        });
        create = Button("Create", async () =>
        {
            if (plan is null || planName != name.Text.Trim() || planParent != parent.Text.Trim()) return;
            var files = plan; string targetName = planName; string targetParent = planParent; bool git = initGit.IsChecked == true;
            await Run(create, status, async () =>
            {
                inspect.IsEnabled = false;
                try
                {
                    string folder = await Task.Run(() => ProjectTemplates.CreateAsync(targetParent!, targetName!, files, git, _closing.Token), _closing.Token);
                    plan = null; status.Text = WorkbenchText.T("Created") + " " + folder; _created(folder);
                }
                finally { if (!_closing.IsCancellationRequested) inspect.IsEnabled = true; }
            });
            create.IsEnabled = plan is not null;
        }); create.IsEnabled = false;
        inspect = Button("Preview", async () =>
        {
            if (templates.SelectedItem is not ProjectTemplate template) return;
            string projectName = name.Text.Trim(), destination = parent.Text.Trim();
            await Run(inspect, status, async () =>
            {
                Invalidate();
                var files = await Task.Run(() => ProjectTemplates.Preview(template, projectName, _closing.Token), _closing.Token);
                if (_closing.IsCancellationRequested || projectName != name.Text.Trim() || destination != parent.Text.Trim() || !Equals(template, templates.SelectedItem)) return;
                plan = files; planName = projectName; planParent = destination;
                preview.Text = Path.Combine(destination, projectName) + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, files.Select(f => $"{f.RelativePath}  ({f.Content.Length:N0} B)"));
                status.Text = WorkbenchText.T("TemplateReviewed"); create.IsEnabled = true;
            });
        });
        AddTop(panel, Text("TemplateHint")); AddTop(panel, Row(templates, register, remove));
        AddTop(panel, Row(Text("ProjectName"), name)); AddTop(panel, Row(Text("Destination"), parent, Button("Browse", () => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(this) == true) parent.Text = picker.FolderName; })));
        AddTop(panel, Row(initGit, inspect, create)); AddTop(panel, status); panel.Children.Add(preview); Reload(); return panel;
    }
}
