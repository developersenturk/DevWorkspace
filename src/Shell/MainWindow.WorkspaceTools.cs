using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class MainWindow
{
    private WorkspaceToolsWindow? _workspaceTools;
    private void WorkspaceToolsClick(object sender, RoutedEventArgs e) => ShowWorkspaceTools(sender is MenuItem { Tag: string index } && int.TryParse(index, out int tab) ? tab : 0);

    private void ShowWorkspaceTools(int tab)
    {
        if (_busy || _workspace is not { } workspace) return;
        if (_workspaceTools is { } existing)
        {
            existing.SelectTab(tab); if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return;
        }
        var window = new WorkspaceToolsWindow(workspace, _projects.ToArray(), tab, () => _session.Templates ?? [], templates =>
        {
            var previous = _session.Templates; _session.Templates = templates;
            if (_session.SaveTo(SessionState.FilePath)) return true;
            _session.Templates = previous; return false;
        }, _closing.Token, folder =>
        {
            // Creating a project does not silently alter the workspace's linked-project settings.
            StatusText.Text = WorkbenchText.T("Created") + " " + folder;
            if (_workspace?.Root == workspace.Root) ScheduleWorkspaceRefresh();
        }, project => OpenGitWorkbench(project), StopDevelopment) { Owner = this };
        _workspaceTools = window; window.Closed += (_, _) => _workspaceTools = null; window.Show();
    }

    private ResumeNote? NoteFor(ProjectEntry project) => _workspace is { } workspace && _session.For(workspace.Root).ResumeNotes is { } notes
        ? notes.FirstOrDefault(n => n.Key.Equals(project.RelativePath, StringComparison.OrdinalIgnoreCase)).Value : null;

    private void RefreshResumeNote(ProjectEntry project)
    {
        var note = NoteFor(project);
        DrawerResumeText.Text = note is null ? WorkbenchText.T("NoNote") :
            WorkbenchText.T("Context") + Environment.NewLine + note.Context + Environment.NewLine + Environment.NewLine +
            WorkbenchText.T("Next") + Environment.NewLine + note.Next + Environment.NewLine + note.UpdatedUtc.ToLocalTime().ToString("g");
    }

    private void ResumeNoteClick(object sender, RoutedEventArgs e)
    {
        var project = _drawerProject ?? ProjectGrid.SelectedItem as ProjectEntry;
        if (project is null || _workspace is not { } workspace) return;
        var window = new ResumeNoteWindow(project.Name, NoteFor(project), note =>
        {
            var previous = _session.For(workspace.Root);
            var notes = new Dictionary<string, ResumeNote>(previous.ResumeNotes ?? [], StringComparer.OrdinalIgnoreCase);
            notes.Remove(project.RelativePath); if (note is not null) notes[project.RelativePath] = note;
            _session.Workspaces[workspace.Root] = previous with { ResumeNotes = notes };
            if (!_session.SaveTo(SessionState.FilePath)) { _session.Workspaces[workspace.Root] = previous; return false; }
            if (_drawerProject?.RelativePath == project.RelativePath) RefreshResumeNote(project);
            return true;
        }) { Owner = this }; window.ShowDialog();
    }
}
