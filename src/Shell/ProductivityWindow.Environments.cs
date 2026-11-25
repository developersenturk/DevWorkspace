using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DevWorkspace;

internal sealed partial class ProductivityWindow
{
    private UIElement EnvironmentsPanel()
    {
        var panel = WorkspaceToolsWindow.Panel(); var source = Profiles(); var project = Projects();
        var name = Edit("VariantProfileName"); var label = Edit("EnvironmentLabel", 150); var file = Edit("EnvironmentFile", 300); var port = Edit("ReadyPort", 70);
        var only = new CheckBox { Content = WorkbenchText.T("VariantSelectedOnly"), Margin = new(0, 6, 8, 8) };
        var grid = Table(("LaunchProfile", "Name", 250), ("EnvironmentLabel", "EnvironmentName", 180), ("Detail", "Detail", 0));
        var details = Output("EnvironmentVariantPreview"); details.Height = 130;
        void Reload()
        {
            var previous = source.SelectedItem as LaunchProfile; source.ItemsSource = _session().Profiles ?? [];
            source.SelectedItem = (_session().Profiles ?? []).FirstOrDefault(p => p.Name == previous?.Name) ?? (_session().Profiles ?? []).FirstOrDefault();
            grid.ItemsSource = (_session().Profiles ?? []).Where(p => p.EnvironmentName is not null).Select(p => new {
                p.Name, p.EnvironmentName, Detail = string.Join(" · ", (p.EnvironmentFiles ?? []).Select(f => f.Key + " → " + f.Value)) }).ToArray();
        }
        void Preview()
        {
            if (source.SelectedItem is not LaunchProfile value) return;
            details.Text = value.Name + System.Environment.NewLine + string.Join(System.Environment.NewLine,
                value.Steps.Select(s => s.Project + " · " + (s.Command?.Display ?? s.Action) + (s.ReadyPort is { } number ? " · TCP " + number : ""))) +
                System.Environment.NewLine + string.Join(System.Environment.NewLine, (value.EnvironmentFiles ?? []).Select(p => p.Key + " → " + p.Value));
        }
        source.SelectionChanged += (_, _) => Preview();
        Button save = null!, start = null!;
        save = WorkspaceToolsWindow.Button("SaveEnvironmentVariant", async () => await Run(save, () =>
        {
            if (source.SelectedItem is not LaunchProfile original || project.SelectedItem is not ProjectEntry selected || string.IsNullOrWhiteSpace(label.Text))
                throw new IOException(WorkbenchText.T("VariantRequired"));
            int? number = null;
            if (!string.IsNullOrWhiteSpace(port.Text))
            { if (!int.TryParse(port.Text, out int parsed) || parsed is < 1 or > 65535) throw new IOException(WorkbenchText.T("InvalidWorkflow")); number = parsed; }
            var variant = LaunchEnvironment.Variant(original, name.Text, label.Text, selected, file.Text.Trim(), number, only.IsChecked == true);
            if (file.Text.Trim().Length > 0) _ = LaunchEnvironment.Read(LaunchEnvironment.Resolve(selected.Folder, file.Text.Trim()));
            if (Persist(_session() with { Profiles = (_session().Profiles ?? []).Where(p => !p.Name.Equals(variant.Name, StringComparison.OrdinalIgnoreCase)).Append(variant).ToArray() }))
            {
                Reload(); source.SelectedItem = (_session().Profiles ?? []).First(p => p.Name == variant.Name);
                _selectProfile(variant.Name); _status.Text = WorkbenchText.T("VariantSaved");
            }
            return Task.CompletedTask;
        }));
        start = WorkspaceToolsWindow.Button("StartSelectedVariant", async () => await Run(start, async () =>
        {
            if (source.SelectedItem is not LaunchProfile value) return; await _start(value);
            _selectProfile(value.Name); _status.Text = WorkbenchText.T("DevelopmentStarted");
        }));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Text("EnvironmentVariantsHint"));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(Field("BaseProfile", source), Field("Project", project), WorkspaceToolsWindow.Button("Refresh", Reload)));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(Field("VariantProfileName", name), Field("EnvironmentLabel", label)));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(Field("EnvironmentFile", file), WorkspaceToolsWindow.Button("Browse", () =>
        {
            if (project.SelectedItem is not ProjectEntry selected) return;
            var picker = new OpenFileDialog { InitialDirectory = selected.Folder, Filter = "Environment files (*.*)|*.*" };
            if (picker.ShowDialog(this) != true) return;
            try { string relative = Path.GetRelativePath(selected.Folder, picker.FileName); _ = LaunchEnvironment.Resolve(selected.Folder, relative); file.Text = relative; }
            catch (IOException ex) { _status.Text = ex.Message; }
            catch (System.IO.InvalidDataException ex) { _status.Text = ex.Message; }
        }), Field("ReadyPort", port), only));
        WorkspaceToolsWindow.AddTop(panel, WorkspaceToolsWindow.Row(save, start)); WorkspaceToolsWindow.AddTop(panel, details);
        panel.Children.Add(grid); Reload(); Preview(); return panel;
    }
}
