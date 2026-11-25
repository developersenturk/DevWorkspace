using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

internal sealed partial class ProductivityWindow
{
    private UIElement WorkSetsPanel()
    {
        var panel = WorkspaceToolsWindow.Panel();
        var name = Edit("WorkSetName"); var notes = Edit("WorkSetNotes", 400, true); notes.Height = 65;
        var profile = Profiles();
        var projects = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = "DisplayPath", ItemsSource = _projects(), MinHeight = 80, Height = 115, Margin = new(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(projects, WorkbenchText.T("WorkSetProjects"));
        var grid = Table(("WorkSetName", "Name", 220), ("LaunchProfile", "Profile", 230), ("Updated", "UpdatedUtc", 170), ("WorkSetNotes", "Notes", 0));
        Guid? selectedId = null;
        void Reload(Guid? id = null)
        {
            var sets = _session().WorkSets ?? []; grid.ItemsSource = sets.OrderByDescending(s => s.UpdatedUtc).ToArray();
            grid.CurrentItem = sets.FirstOrDefault(s => s.Id == id);
        }
        void UseProfileProjects()
        {
            if (profile.SelectedItem is not LaunchProfile value) return;
            projects.SelectedItems.Clear();
            foreach (var project in _projects().Where(p => value.Steps.Any(s => s.Project.Equals(p.RelativePath, StringComparison.OrdinalIgnoreCase)))) projects.SelectedItems.Add(project);
        }
        grid.CurrentItemChanged += (_, _) =>
        {
            if (grid.CurrentItem is not WorkSet saved) return;
            selectedId = saved.Id; name.Text = saved.Name; notes.Text = saved.Notes;
            profile.ItemsSource = _session().Profiles ?? [];
            profile.SelectedItem = (_session().Profiles ?? []).FirstOrDefault(p => p.Name == saved.Profile);
            projects.SelectedItems.Clear();
            foreach (var project in _projects().Where(p => saved.Projects.Any(s => s.Project.Equals(p.RelativePath, StringComparison.OrdinalIgnoreCase)))) projects.SelectedItems.Add(project);
        };
        Button save = null!, restore = null!;
        save = WorkspaceToolsWindow.Button("SaveWorkSet", async () => await Run(save, async () =>
        {
            var chosen = projects.SelectedItems.Cast<ProjectEntry>().ToArray();
            string title = name.Text.Trim();
            if ((_session().WorkSets ?? []).Any(s => s.Id != selectedId && s.Name.Equals(title, StringComparison.OrdinalIgnoreCase)))
                throw new System.IO.IOException(WorkbenchText.T("DuplicateWorkSet"));
            var saved = await WorkSets.CaptureAsync(selectedId ?? Guid.NewGuid(), title, notes.Text,
                (profile.SelectedItem as LaunchProfile)?.Name, chosen, _session(), _closing.Token);
            _closing.Token.ThrowIfCancellationRequested();
            if (Persist(_session() with { WorkSets = (_session().WorkSets ?? []).Where(s => s.Id != saved.Id).Append(saved).ToArray() }))
            { Reload(saved.Id); _status.Text = WorkbenchText.T("WorkSetSaved"); }
        }));
        var start = new CheckBox { Content = WorkbenchText.T("StartWorkSetServices"), Margin = new(0, 6, 8, 8) };
        restore = WorkspaceToolsWindow.Button("RestoreWorkSet", async () => await Run(restore, async () =>
        {
            if (grid.CurrentItem is not WorkSet saved) throw new System.IO.IOException(WorkbenchText.T("WorkSetRequired"));
            await _restore(saved, start.IsChecked == true, _closing.Token);
            if (!_closing.IsCancellationRequested) { _selectProfile(saved.Profile ?? ""); _status.Text = WorkbenchText.T("WorkSetRestored"); }
        }));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Text("WorkSetsHint"));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(Field("WorkSetName", name), Field("LaunchProfile", profile),
            WorkspaceToolsWindow.Button("UseProfileProjects", UseProfileProjects)));
        WorkspaceToolsWindow.AddTop(panel, Field("WorkSetNotes", notes));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Text("WorkSetProjects")); WorkspaceToolsWindow.AddTop(panel, projects);
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(save,
            WorkspaceToolsWindow.Button("NewWorkSet", () => { selectedId = null; grid.CurrentItem = null; name.Clear(); notes.Clear(); projects.SelectedItems.Clear(); }),
            restore, start, WorkspaceToolsWindow.Button("RemoveWorkSet", () =>
            {
                if (_working || grid.CurrentItem is not WorkSet saved) return;
                if (Persist(_session() with { WorkSets = (_session().WorkSets ?? []).Where(s => s.Id != saved.Id).ToArray() }))
                { selectedId = null; Reload(); name.Clear(); notes.Clear(); }
            })));
        panel.Children.Add(grid); Reload(_session().LastWorkSet); if (projects.SelectedItems.Count == 0 && projects.Items.Count > 0) projects.SelectedIndex = 0;
        return panel;
    }
}
