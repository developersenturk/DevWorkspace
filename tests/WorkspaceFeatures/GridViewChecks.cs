using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevExpress.Xpf.Grid;
using DevWorkspace;

internal static class GridViewChecks
{
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var match in Find<T>(child)) yield return match;
    }

    internal static void Run(WorkspaceToolsWindow tools, ProjectEntry project, Action<bool, string> check)
    {
        var grids = Find<GridControl>(tools).Distinct().ToArray();
        check(grids.Length == 5, "All five workspace tool tables use DevExpress");
        check(!Find<DataGrid>(tools).Any(), "Workspace tools contain no standard WPF table");
        foreach (var grid in grids)
        {
            check(grid.View is TableView { AllowEditing: false, ShowGroupPanel: false }, "Tool grids preserve read-only row navigation");
            check(grid.Columns.All(c => c.ReadOnly), "Tool table columns remain read-only");
        }

        var saved = OperationCenter.Entries.ToArray();
        OperationCenter.Entries.Clear();
        try
        {
            bool canceled = false; int retries = 0;
            var failed = OperationCenter.Start("Failed fixture", "Target A", () => { retries++; OperationCenter.Start("Retry", "Target A"); });
            failed.Finish(Loc.Text("LF0873175A881"), "Failed output");
            var running = OperationCenter.Start("Running fixture", "Target B");
            running.Cancel = () => canceled = true;
            running.Append("Running output");
            var window = new OperationCenterWindow();
            window.Show(); window.UpdateLayout(); Pump();
            var grid = Find<GridControl>(window).Single();
            var output = ((Grid)window.Content).Children.OfType<TextBox>().Single();
            var buttons = ((Grid)window.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            check(grid.CurrentItem == running && output.Text == "Running output", $"Operation center selects the newest operation and its output (current={grid.CurrentItem}, text={output.Text}, loaded={window.IsLoaded}, rows={grid.VisibleRowCount})");
            check(buttons[0].IsEnabled && !buttons[1].IsEnabled, "Only stop is enabled for a running operation");
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(canceled, "Stop targets the current operation");
            grid.Columns[0].SortOrder = DevExpress.Data.ColumnSortOrder.Ascending;
            grid.CurrentItem = failed;
            check(output.Text.Contains("Failed output") && buttons[1].IsEnabled && !buttons[0].IsEnabled, "Sorted operation selection updates output and actions");
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(retries == 1, "Retry invokes the selected failed operation");
            grid.CurrentItem = null;
            check(output.Text.Length == 0 && !buttons[0].IsEnabled && !buttons[1].IsEnabled, "Clearing operation selection resets output and actions");
            window.Close();
        }
        finally
        {
            OperationCenter.Entries.Clear();
            foreach (var entry in saved) OperationCenter.Entries.Add(entry);
        }

        var finding = new TrackedFinding("fixture", "Example", "Title", "Detail", "Target", DateTimeOffset.Now);
        var findingsWindow = new TrackedFindingsWindow([finding], _ => { });
        findingsWindow.Show(); findingsWindow.UpdateLayout(); Pump();
        var findings = Find<GridControl>(findingsWindow).Single();
        check(ReferenceEquals(findings.CurrentItem, finding), "Tracked findings select the first record before an action");
        check(findings.GetCellValue(0, "Due.LocalDateTime") is DateTime, "Nested date binding remains a date");
        findingsWindow.Close();

        // No Git command or fetch is started by this editor test.
        var fetch = new BatchFetchWindow([project with { Repository = Path.GetTempPath() }], CancellationToken.None);
        fetch.Show(); fetch.UpdateLayout(); Pump();
        var fetchGrid = Find<GridControl>(fetch).Single();
        var view = (TableView)fetchGrid.View;
        foreach (double width in new[] { 1100d, 700d, 530d })
        {
            fetch.Width = width; fetch.UpdateLayout(); Pump();
            check(fetchGrid.Columns.All(c => c.ActualWidth >= c.MinWidth - 1), "Fetch columns remain readable at window width " + width);
            check(fetchGrid.Columns[1].ActualWidth >= 259 && fetchGrid.Columns[5].ActualWidth >= 219, "Repository and result columns cannot collapse");
        }
        check(view.AllowEditing && fetchGrid.Columns.Skip(1).All(c => c.ReadOnly), "Only the fetch inclusion column can be edited");
        fetchGrid.CurrentItem = ((IEnumerable)fetchGrid.ItemsSource).Cast<object>().First();
        view.FocusedRowHandle = 0;
        fetchGrid.CurrentColumn = fetchGrid.Columns[0];
        view.Focus(); Pump();
        view.ShowEditor();
        Pump();
        check(view.ActiveEditor is not null, "Fetch checkbox editor is available");
        view.ActiveEditor!.EditValue = false;
        check(view.CommitEditing(), "Fetch selection is committed before starting an operation");
        check(fetchGrid.GetCellValue(0, "Included") is false, "Unchecking a fetch row reaches the source object");
        fetch.Close();

        var inspection = new ProjectInspectionWindow("Inspection fixture", _ => Task.FromResult(new[] { new InspectionRow("OK", "Name", "Detail") }));
        check(Find<GridControl>(inspection).Single().Columns.Count == 3, "Project inspection uses a three-column DevExpress table");
        inspection.Close();
        var worktrees = new GitWorktreeWindow(Path.GetTempPath(), _ => true);
        check(Find<GridControl>(worktrees).Single().Columns.Count == 4, "Worktree management uses a four-column DevExpress table");
        worktrees.Close();
    }
}
