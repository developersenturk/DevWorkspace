using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal sealed class ResumeNoteWindow : ThemedWindow
{
    internal ResumeNoteWindow(string project, ResumeNote? note, Func<ResumeNote?, bool> save)
    {
        Title = WorkbenchText.T("ResumeNote") + " · " + project; Width = 620; Height = 510; MinWidth = 420; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = WorkspaceToolsWindow.Panel();
        var context = new TextBox { Text = note?.Context ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 12000, Margin = new(0, 0, 0, 12) };
        var next = new TextBox { Text = note?.Next ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 12000 };
        AutomationProperties.SetName(context, WorkbenchText.T("Context")); AutomationProperties.SetName(next, WorkbenchText.T("Next"));
        var status = WorkspaceToolsWindow.Text("NoteHint");
        var buttons = WorkspaceToolsWindow.Row(WorkspaceToolsWindow.Button("Save", () =>
        {
            ResumeNote? value = string.IsNullOrWhiteSpace(context.Text) && string.IsNullOrWhiteSpace(next.Text) ? null : new(context.Text.Trim(), next.Text.Trim(), DateTime.UtcNow);
            if (!save(value)) { status.Text = WorkbenchText.T("SaveFailed"); return; } DialogResult = true;
        }), WorkspaceToolsWindow.Button("Cancel", Close));
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        WorkspaceToolsWindow.AddTop(panel, status);
        var fields = new Grid();
        fields.RowDefinitions.Add(new() { Height = GridLength.Auto }); fields.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        fields.RowDefinitions.Add(new() { Height = GridLength.Auto }); fields.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var contextLabel = WorkspaceToolsWindow.Text("Context"); var nextLabel = WorkspaceToolsWindow.Text("Next");
        Grid.SetRow(context, 1); Grid.SetRow(nextLabel, 2); Grid.SetRow(next, 3);
        fields.Children.Add(contextLabel); fields.Children.Add(context); fields.Children.Add(nextLabel); fields.Children.Add(next);
        panel.Children.Add(fields); Content = panel;
    }
}
