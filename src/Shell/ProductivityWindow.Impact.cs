using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

internal sealed partial class ProductivityWindow
{
    private UIElement ImpactPanel()
    {
        var panel = WorkspaceToolsWindow.Panel(); var project = Projects();
        var changed = new CheckBox { Content = WorkbenchText.T("ImpactChangedOnly"), Margin = new(0, 6, 8, 8) };
        var grid = Table(("Project", "Name", 170), ("Relationship", "Relationship", 130), ("Manifest", "Manifest", 0), ("TestCommand", "TestCommand", 360));
        var notes = Output("ImpactDiagnostics"); notes.Height = 80;
        ImpactReport? report = null; string? selectedPath = null; bool changedSelection = false;
        Button analyze = null!, run = null!;
        void Invalidate() { report = null; if (run is not null) run.IsEnabled = false; }
        project.SelectionChanged += (_, _) => Invalidate(); changed.Checked += (_, _) => Invalidate(); changed.Unchecked += (_, _) => Invalidate();
        analyze = WorkspaceToolsWindow.Button("AnalyzeImpact", async () => await Run(analyze, async () =>
        {
            if (project.SelectedItem is not ProjectEntry selected) return;
            var snapshot = _projects(); bool only = changed.IsChecked == true;
            if (selected.Repository is { } repository) selected = selected with { Git = await GitStatus.ReadAsync(repository, _closing.Token) };
            var result = await Task.Run(() => ImpactAnalysis.Read(selected, snapshot, _workspace.Config.ScanExclusions, only, _closing.Token), _closing.Token);
            _closing.Token.ThrowIfCancellationRequested();
            if ((project.SelectedItem as ProjectEntry)?.RelativePath != selected.RelativePath || (changed.IsChecked == true) != only) return;
            report = result; selectedPath = selected.RelativePath; changedSelection = only;
            grid.ItemsSource = result.Targets; run.IsEnabled = result.Targets.Any(t => t.Test is not null);
            notes.Text = string.Join(Environment.NewLine, result.Diagnostics);
            _status.Text = string.Format(WorkbenchText.T("ImpactResult"), result.Seeds.Length, result.Targets.Length, result.Targets.Count(t => t.Test is not null));
        }));
        run = WorkspaceToolsWindow.Button("RunRelevantTests", async () => await Run(run, async () =>
        {
            if (report is null || (project.SelectedItem as ProjectEntry)?.RelativePath != selectedPath || (changed.IsChecked == true) != changedSelection) return;
            foreach (var target in report.Targets.Where(t => t.Test is not null))
            {
                _closing.Token.ThrowIfCancellationRequested();
                var execution = OperationCenter.StartCommand(target.Test!, target.Name, target.Folder, _workbench);
                await execution.Completion;
                if (execution.Operation.IsFailed) { _status.Text = WorkbenchText.T("StepFailed"); _tools(2); return; }
            }
            _status.Text = WorkbenchText.T("RelevantTestsFinished"); _tools(2);
        })); run.IsEnabled = false;
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Text("ImpactAnalysisHint"));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(project, changed, analyze, run)); WorkspaceToolsWindow.AddTop(panel, notes);
        panel.Children.Add(grid); return panel;
    }
}
