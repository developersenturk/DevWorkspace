using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

internal sealed partial class ProductivityWindow
{
    private sealed record OperationChoice(WorkspaceOperation Operation)
    { public string Label => Operation.Target + " · " + Operation.Name + " · " + Operation.Status; }

    private UIElement DiagnosticsPanel()
    {
        var panel = WorkspaceToolsWindow.Panel(); var project = Projects();
        var operations = new ComboBox { MinWidth = 380, DisplayMemberPath = "Label", Margin = new(0, 0, 8, 8) };
        System.Windows.Automation.AutomationProperties.SetName(operations, WorkbenchText.T("OperationLog"));
        var log = Edit("DiagnosticLog", 0, true); log.Height = 145; log.FontFamily = new("Consolas");
        var detail = Output("DiagnosticDetail"); detail.Height = 95;
        var grid = Table(("Solution", "Title", 230), ("Detail", "Detail", 0), ("Command", "Command", 330));
        void Reload()
        {
            operations.ItemsSource = OperationCenter.Entries.Where(o => o.Folder is { } folder && _projects().Any(p => p.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                .Select(o => new OperationChoice(o)).ToArray(); operations.SelectedIndex = 0;
        }
        operations.SelectionChanged += (_, _) =>
        {
            if (operations.SelectedItem is not OperationChoice choice) return;
            log.Text = choice.Operation.ReadOutput(); project.SelectedItem = ((IEnumerable<ProjectEntry>)project.ItemsSource).FirstOrDefault(p => p.Folder.Equals(choice.Operation.Folder, StringComparison.OrdinalIgnoreCase));
        };
        Button fix = null!, ports = null!;
        var install = WorkspaceToolsWindow.Button("InstallTools", () => _install((grid.CurrentItem as DiagnosticSolution)?.Tool));
        void Selected()
        {
            var solution = grid.CurrentItem as DiagnosticSolution;
            detail.Text = solution is null ? "" : solution.Detail + Environment.NewLine + Environment.NewLine + solution.Command;
            if (fix is not null) fix.IsEnabled = solution?.Fix is not null;
            if (ports is not null) ports.IsEnabled = solution?.Port is not null;
            install.IsEnabled = solution?.Tool is not null;
        }
        grid.CurrentItemChanged += (_, _) => Selected();
        fix = WorkspaceToolsWindow.Button("RunSuggestedCommand", async () => await Run(fix, async () =>
        {
            if (grid.CurrentItem is not DiagnosticSolution { Fix: { } command } || project.SelectedItem is not ProjectEntry selected) return;
            var execution = OperationCenter.StartCommand(command, selected.Name, selected.Folder, _workbench);
            await execution.Completion;
            log.Text = execution.Operation.ReadOutput();
            _status.Text = execution.Operation.IsFailed ? WorkbenchText.T("StepFailed") : WorkbenchText.T("SuggestedCommandFinished");
        }));
        ports = WorkspaceToolsWindow.Button("InspectPortOwners", async () => await Run(ports, async () =>
        {
            if (grid.CurrentItem is not DiagnosticSolution { Port: { } port }) return;
            var owners = await DiagnosticSolutions.PortOwnersAsync(port, _closing.Token);
            detail.Text = owners.Length == 0 ? WorkbenchText.T("PortAvailable") : string.Join(Environment.NewLine, owners.Select(o => $"PID {o.Pid} · {o.Process} · {o.Address}"));
            _status.Text = WorkbenchText.T("PortOwnersHint");
        }));
        var analyze = WorkspaceToolsWindow.Button("AnalyzeLog", () =>
        {
            try
            {
                if (project.SelectedItem is not ProjectEntry selected) return;
                var rows = DiagnosticSolutions.Analyze(log.Text, selected); grid.ItemsSource = rows; WorkbenchGrid.SelectFirst(grid); Selected();
                _status.Text = rows.Length == 0 ? WorkbenchText.T("DiagnosticNoMatch") : string.Format(WorkbenchText.T("DiagnosticCount"), rows.Length);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { _status.Text = WorkbenchText.T("DiagnosticNoMatch"); }
        });
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Text("DiagnosticSolutionsHint"));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(Field("Project", project), operations, WorkspaceToolsWindow.Button("Refresh", Reload)));
        WorkspaceToolsWindow.AddTop(panel, Field("DiagnosticLog", log));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(analyze, fix, install, ports,
            WorkspaceToolsWindow.Button("Environment", () => _tools(5)), WorkspaceToolsWindow.Button("Runtime", () => _tools(2))));
        WorkspaceToolsWindow.AddTop(panel, detail); panel.Children.Add(grid); Reload(); Selected(); return panel;
    }
}
