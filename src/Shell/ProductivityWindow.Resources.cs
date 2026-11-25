using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace DevWorkspace;

internal sealed partial class ProductivityWindow
{
    private bool _sampling;
    private UIElement ResourcesPanel()
    {
        var panel = WorkspaceToolsWindow.Panel(); var sampler = new ManagedResourceSampler();
        var resources = Table(("Project", "Project", 180), ("Command", "Command", 190), ("Pid", "Pid", 80), ("CpuPercent", "CpuPercent", 95),
            ("MemoryMb", "MemoryMb", 110), ("ProcessCount", "Processes", 100)); resources.Height = 160;
        resources.Columns.First(c => c.FieldName == "CpuPercent").EditSettings = new DevExpress.Xpf.Editors.Settings.TextEditSettings { DisplayFormat = "N1" };
        resources.Columns.First(c => c.FieldName == "MemoryMb").EditSettings = new DevExpress.Xpf.Editors.Settings.TextEditSettings { DisplayFormat = "N1" };
        var artifacts = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = "Label", MinHeight = 110 };
        AutomationProperties.SetName(artifacts, WorkbenchText.T("CleanupCandidates"));
        var preview = Output("CleanupPreview"); preview.Height = 115;
        var summary = WorkspaceToolsWindow.Text("CleanupNotScanned");
        ArtifactReport? report = null;
        async void Sample()
        {
            if (_sampling || _closing.IsCancellationRequested || _tabs.SelectedIndex != 4) return;
            _sampling = true;
            try
            {
                var operations = OperationCenter.Entries.Where(o => o.IsRunning && o.Folder is { } folder && _projects().Any(p => p.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))).ToArray();
                var rows = await Task.Run(() => sampler.Read(operations, _closing.Token), _closing.Token);
                if (!_closing.IsCancellationRequested) await Dispatcher.InvokeAsync(() => resources.ItemsSource = rows);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { if (!_closing.IsCancellationRequested) await Dispatcher.InvokeAsync(() => _status.Text = ex.Message); }
            finally { _sampling = false; }
        }
        _timer.Tick += (_, _) => Sample(); _tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, _tabs)) Sample(); };
        artifacts.SelectionChanged += (_, _) =>
        {
            var selected = artifacts.SelectedItems.Cast<ArtifactCandidate>().ToArray();
            preview.Text = string.Join(Environment.NewLine + Environment.NewLine, selected.Select(c => c.Folder + Environment.NewLine + c.Detail + Environment.NewLine +
                string.Join(Environment.NewLine, c.Entries.Where(e => !e.Directory).Take(300).Select(e => e.RelativePath + " · " + e.Bytes + " B")) +
                (c.Files > 300 ? Environment.NewLine + WorkbenchText.T("CleanupPreviewLimited") : "")));
            if (report is not null) summary.Text = string.Format(WorkbenchText.T("CleanupSelected"), selected.Length, selected.Sum(c => c.Bytes) / 1048576d);
        };
        Button scan = null!, recycle = null!, permanent = null!;
        async Task Clean(bool delete, Button button)
        {
            var selected = artifacts.SelectedItems.Cast<ArtifactCandidate>().ToArray();
            if (selected.Length == 0 || _working) return;
            string message = string.Format(WorkbenchText.T(delete ? "CleanupPermanentConfirm" : "CleanupRecycleConfirm"), selected.Length, selected.Sum(c => c.Bytes) / 1048576d) +
                Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, selected.Select(c => c.Folder));
            if (MessageBox.Show(this, message, WorkbenchText.T("ResourcesCleanup"), MessageBoxButton.YesNo,
                delete ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await Run(button, async () =>
            {
                await Task.Run(() => ArtifactCleanup.CleanAsync(selected, ActiveFolders, delete, _closing.Token), _closing.Token);
                report = null; artifacts.ItemsSource = null; preview.Clear(); summary.Text = WorkbenchText.T("CleanupNotScanned");
                _status.Text = WorkbenchText.T(delete ? "CleanupDeleted" : "CleanupRecycled");
            });
        }
        scan = WorkspaceToolsWindow.Button("ScanArtifacts", async () => await Run(scan, async () =>
        {
            var projects = _projects();
            var result = await Task.Run(() => ArtifactCleanup.Scan(projects, _closing.Token), _closing.Token);
            // Check Git and running commands during preview too; cleanup repeats these checks immediately before moving files.
            var active = ActiveFolders();
            var checkedCandidates = await Task.Run(async () =>
            {
                var items = new List<ArtifactCandidate>();
                foreach (var candidate in result.Candidates)
                {
                    if (!candidate.CanClean) { items.Add(candidate); continue; }
                    try { await ArtifactCleanup.ValidateAsync(candidate, active, _closing.Token); items.Add(candidate); }
                    catch (System.IO.IOException ex) { items.Add(candidate with { CanClean = false, Detail = ex.Message }); }
                }
                return items;
            }, _closing.Token);
            _closing.Token.ThrowIfCancellationRequested(); report = result with { Candidates = checkedCandidates.ToArray() };
            artifacts.ItemsSource = report.Candidates; summary.Text = string.Format(WorkbenchText.T("CleanupScanResult"), report.Candidates.Length, report.Candidates.Sum(c => c.Bytes) / 1048576d);
            preview.Text = string.Join(Environment.NewLine, report.Diagnostics); _status.Text = WorkbenchText.T("CleanupReview");
        }));
        recycle = WorkspaceToolsWindow.Button("RecycleArtifacts", async () => await Clean(false, recycle));
        permanent = WorkspaceToolsWindow.Button("DeleteArtifacts", async () => await Clean(true, permanent));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Text("ResourcesHint")); WorkspaceToolsWindow.AddTop(panel, resources);
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Text("CleanupHint"));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(scan, WorkspaceToolsWindow.Button("SelectSafeArtifacts", () =>
        { artifacts.SelectedItems.Clear(); foreach (var candidate in report?.Candidates.Where(c => c.CanClean) ?? []) artifacts.SelectedItems.Add(candidate); }), recycle, permanent));
        WorkspaceToolsWindow.AddTop(panel, summary); DockPanel.SetDock(preview, Dock.Bottom); panel.Children.Add(preview); panel.Children.Add(artifacts);
        artifacts.Height = 180; panel.MinHeight = 710;
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
