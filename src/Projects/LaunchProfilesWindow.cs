using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal sealed class LaunchProfilesWindow : ThemedWindow
{
    private sealed record Option(ProjectCommand? Command, string Action = "default")
    {
        public string Label => Action == "terminal" ? WorkbenchText.T("Terminal") : Command is null ? Loc.Text("L913A7BCCDD79") : $"{Command.Name} · {Command.Display}";
    }

    private sealed record Step(LaunchStep Value, string Label);
    private readonly IReadOnlyList<ProjectEntry> _projects;
    private readonly Action<LaunchProfile[]> _save;
    private readonly ComboBox _profiles = new() { MinWidth = 185, DisplayMemberPath = "Name" };
    private readonly ComboBox _project = new() { MinWidth = 150, DisplayMemberPath = "Name" };
    private readonly ComboBox _command = new() { MinWidth = 230, DisplayMemberPath = "Label" };
    private readonly ListBox _steps = new() { DisplayMemberPath = "Label", Margin = new(0, 8, 0, 8) };
    private readonly TextBox _name = new() { MinWidth = 185 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private LaunchProfile[] _saved;
    private readonly string _root;
    private readonly List<Step> _draft = [];
    internal LaunchProfile? SelectedProfile { get; private set; }

    internal LaunchProfilesWindow(IReadOnlyList<ProjectEntry> projects, LaunchProfile[] saved, Action<LaunchProfile[]> save, string root)
    {
        _root = root;
        _projects = projects;
        _saved = saved;
        _save = save;
        Title = Loc.Text("LC12C038CF0C7");
        Width = 800;
        Height = 470;
        MinWidth = 620;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _profiles.ItemsSource = _saved;
        _profiles.SelectionChanged += (_, _) => Load(_profiles.SelectedItem as LaunchProfile);
        _project.ItemsSource = projects;
        _project.SelectionChanged += (_, _) =>
        {
            if (_project.SelectedItem is ProjectEntry project)
            {
                var options = new List<Option> { new(null), new(null, "terminal") };
                options.AddRange(ProjectTools.Presets(project).Concat(project.Preference.Commands ?? []).Select(command => new Option(command)));
                if (ProjectIntegrations.HasCompose(project)) options.Add(new(new(WorkbenchText.T("ComposeStart"), "docker.exe", "compose up")));
                _command.ItemsSource = options;
                _command.SelectedIndex = 0;
            }
        };
        _project.SelectedIndex = 0;
        var body = new DockPanel { Margin = new(12) };
        AddDocked(body, Row(Label(Loc.Text("LDF05DDAFD346")), _profiles, Button(Loc.Text("LA7BB8F9DCEB5"), () => { _profiles.SelectedIndex = -1; Load(null); }),
            Button(Loc.Text("LF698E9B40F9F"), Delete)), Dock.Top);
        AddDocked(body, Row(Label(Loc.Text("LD9A085BAB7E5")), _name), Dock.Top);
        AddDocked(body, Row(Label(Loc.Text("L7B321D627CB1")), _project, _command, Button(Loc.Text("L150474518D2F"), AddStep)), Dock.Top);
        AddDocked(body, _status, Dock.Bottom);
        AddDocked(body, Row(Button(Loc.Text("L8C9EFC5978DA"), RemoveStep), Button(Loc.Text("LC1FE2ECDFF02"), Save),
            Button(Loc.Text("ShareExport"), Export), Button(Loc.Text("ShareImport"), Import),
            Button(Loc.Text("L6F66590C9715"), Run)), Dock.Bottom);
        body.Children.Add(_steps);
        Content = body;
        _profiles.SelectedIndex = 0;
    }

    private static void AddDocked(DockPanel parent, UIElement item, Dock position)
    {
        DockPanel.SetDock(item, position);
        parent.Children.Add(item);
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static WrapPanel Row(params FrameworkElement[] items)
    {
        var row = new WrapPanel { Margin = new(0, 0, 0, 5) };
        foreach (var item in items)
        {
            item.Margin = new(0, 0, 8, 3);
            row.Children.Add(item);
        }
        return row;
    }

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new(8, 3, 8, 3) };
        button.Click += (_, _) => action();
        return button;
    }

    private void Load(LaunchProfile? profile)
    {
        _name.Text = profile?.Name ?? "";
        _draft.Clear();
        foreach (var step in profile?.Steps ?? [])
        {
            string projectName = _projects.FirstOrDefault(project =>
                project.RelativePath.Equals(step.Project, StringComparison.OrdinalIgnoreCase))?.Name ?? step.Project;
            _draft.Add(new(step, $"{projectName} · {(step.Action == "terminal" ? WorkbenchText.T("Terminal") : step.Command?.Name ?? Loc.Text("L913A7BCCDD79"))}"));
        }
        RefreshSteps();
        _status.Text = "";
    }

    private void AddStep()
    {
        if (_project.SelectedItem is ProjectEntry project && _command.SelectedItem is Option option)
        {
            var step = new LaunchStep(project.RelativePath, option.Command, option.Action);
            if (_draft.All(row => row.Value != step))
                _draft.Add(new(step, $"{project.Name} · {option.Label}"));
            RefreshSteps();
        }
    }

    private void RemoveStep()
    {
        if (_steps.SelectedItem is Step step && _draft.Remove(step))
            RefreshSteps();
    }

    private void RefreshSteps() => _steps.ItemsSource = _draft.ToArray();

    private LaunchProfile? ReadDraft()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0 || _draft.Count == 0)
        {
            _status.Text = Loc.Text("LDA8AE81676F9");
            return null;
        }
        if (_saved.Any(item => !ReferenceEquals(item, _profiles.SelectedItem) &&
            item.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            _status.Text = Loc.Text("L41A9143D6D12");
            return null;
        }
        return new(name, _draft.Select(row => row.Value).ToArray());
    }

    private void Save()
    {
        if (ReadDraft() is not { } profile)
            return;
        Persist([.. _saved.Where(item => !ReferenceEquals(item, _profiles.SelectedItem)), profile], profile);
        _status.Text = Loc.Text("L736252ABF24A");
    }

    private void Delete()
    {
        if (_profiles.SelectedItem is LaunchProfile profile)
            Persist(_saved.Where(item => !ReferenceEquals(item, profile)).ToArray(), null);
    }

    private void Persist(LaunchProfile[] profiles, LaunchProfile? selected)
    {
        _saved = profiles;
        _save(profiles);
        _profiles.ItemsSource = profiles;
        _profiles.SelectedItem = selected;
    }

    private void Run()
    {
        if (ReadDraft() is { } profile)
        {
            SelectedProfile = profile;
            DialogResult = true;
        }
    }

    private void Export()
    {
        if (ReadDraft() is not { } profile) return;
        var picker = new SaveFileDialog { FileName = "DevWorkspace-profile.json", Filter = "JSON (*.json)|*.json", DefaultExt = ".json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            WorkspaceSharing.WriteAtomic(picker.FileName, WorkspaceSharing.ExportProfile(profile, _projects, _root));
            _status.Text = Loc.Text("GrowthSaved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
    }

    private void Import()
    {
        var picker = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 1024 * 1024) throw new InvalidDataException(Loc.Text("ShareInvalid"));
            var profile = WorkspaceSharing.ImportProfile(File.ReadAllText(picker.FileName), _projects, _root);
            string preview = profile.Name + "\n\n" + string.Join("\n", profile.Steps.Select(step =>
                step.Project + " · " + (step.Command?.Display ?? Loc.Text("L913A7BCCDD79"))));
            if (MessageBox.Show(this, Loc.Text("ShareReview") + "\n\n" + preview, Loc.Text("ShareImport"),
                MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            _profiles.SelectedIndex = -1;
            Load(profile);
            _status.Text = Loc.Text("ShareImported");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _status.Text = ex.Message; }
    }
}
