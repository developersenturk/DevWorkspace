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

internal sealed partial class ProductivityWindow : ThemedWindow
{
    private readonly Workspace _workspace;
    private readonly Func<ProjectEntry[]> _projects;
    private readonly Func<WorkspaceSession> _session;
    private readonly Func<WorkspaceSession, bool> _save;
    private readonly Func<WorkSet, bool, CancellationToken, Task> _restore;
    private readonly Func<LaunchProfile, Task> _start;
    private readonly Action<string> _selectProfile;
    private readonly Action<int> _tools;
    private readonly Action<DevelopmentTool?> _install;
    private readonly CancellationToken _workbench;
    private readonly CancellationTokenSource _closing = new();
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _working;

    internal ProductivityWindow(Workspace workspace, Func<ProjectEntry[]> projects, Func<WorkspaceSession> session,
        Func<WorkspaceSession, bool> save, Func<WorkSet, bool, CancellationToken, Task> restore, Func<LaunchProfile, Task> start,
        Action<string> selectProfile, Action<int> tools, Action<DevelopmentTool?> install, CancellationToken workbench, int tab = 0)
    {
        _workspace = workspace; _projects = projects; _session = session; _save = save; _restore = restore; _start = start;
        _selectProfile = selectProfile; _tools = tools; _install = install; _workbench = workbench;
        Title = WorkbenchText.T("Productivity"); Width = 1160; Height = 800; MinWidth = 880; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Add("WorkSets", WorkSetsPanel()); Add("DiagnosticSolutions", DiagnosticsPanel()); Add("ImpactAnalysis", ImpactPanel());
        Add("EnvironmentVariants", EnvironmentsPanel()); Add("ResourcesCleanup", ResourcesPanel());
        var panel = WorkspaceToolsWindow.Panel(); var heading = WorkspaceToolsWindow.Text("Productivity"); heading.FontSize = 22; heading.FontWeight = FontWeights.SemiBold;
        WorkspaceToolsWindow.AddTop(panel, heading);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status); panel.Children.Add(_tabs); Content = panel;
        SelectTab(tab);
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _closing.Cancel(); };
    }
    internal void SelectTab(int index) => _tabs.SelectedIndex = Math.Clamp(index, 0, 4);
    private void Add(string key, UIElement content)
    {
        var header = WorkspaceToolsWindow.Text(key); header.Margin = new(4, 2, 4, 2); header.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        _tabs.Items.Add(new TabItem { Header = header, Content = content });
    }
    private static TextBox Edit(string key, double width = 220, bool multiline = false)
    {
        var input = WorkspaceToolsWindow.Input(key, width); input.AcceptsReturn = multiline; input.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (multiline) input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; return input;
    }
    private static TextBox Output(string key) { var box = Edit(key, 0, true); box.IsReadOnly = true; box.FontFamily = new("Consolas"); return box; }
    private static UIElement Field(string key, FrameworkElement input) => WorkspaceToolsWindow.Row(WorkspaceToolsWindow.Text(key), input);
    private ComboBox Projects()
    {
        var combo = new ComboBox { MinWidth = 230, DisplayMemberPath = "Name", Margin = new(0, 0, 8, 8), ItemsSource = _projects() };
        AutomationProperties.SetName(combo, WorkbenchText.T("Project")); combo.SelectedIndex = 0; return combo;
    }
    private ComboBox Profiles()
    {
        var combo = new ComboBox { MinWidth = 250, DisplayMemberPath = "Name", Margin = new(0, 0, 8, 8), ItemsSource = _session().Profiles ?? [] };
        AutomationProperties.SetName(combo, WorkbenchText.T("LaunchProfile")); combo.SelectedIndex = 0; return combo;
    }
    private static GridControl Table(params (string Header, string Property, double Width)[] columns)
    {
        var grid = WorkbenchGrid.Create(string.Join(" · ", columns.Select(c => WorkbenchText.T(c.Header))));
        foreach (var c in columns) grid.Columns.Add(WorkbenchGrid.Column(WorkbenchText.T(c.Header), c.Property,
            c.Width == 0 ? new(1, GridColumnUnitType.Star) : new(c.Width)));
        return grid;
    }
    private bool Persist(WorkspaceSession session)
    {
        if (_save(session)) return true;
        _status.Text = WorkbenchText.T("SaveFailed"); return false;
    }
    private async Task Run(Button button, Func<Task> action)
    {
        if (_working) return; _working = true; button.IsEnabled = false; _status.Text = WorkbenchText.T("Working");
        try { await action(); }
        catch (OperationCanceledException) { if (!_closing.IsCancellationRequested) _status.Text = WorkbenchText.T("Cancelled"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or JsonException or
            ArgumentException or InvalidOperationException or System.Xml.XmlException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        { if (!_closing.IsCancellationRequested) _status.Text = ex.Message; }
        finally { _working = false; if (!_closing.IsCancellationRequested) button.IsEnabled = true; }
    }
    private string[] ActiveFolders() => Dispatcher.Invoke(() => OperationCenter.Entries.Where(o => o.IsRunning && o.Folder is not null)
        .Select(o => o.Folder!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
}
