using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using DevExpress.Xpf.Core;
using Microsoft.Win32;

namespace DevWorkspace;

public partial class VisualStudioPickerWindow : ThemedWindow
{
    private readonly ObservableCollection<VisualStudioInstallation> _choices;
    internal string? SelectedExecutable => (Installations.SelectedItem as VisualStudioInstallation)?.ExecutablePath;

    internal VisualStudioPickerWindow(string project, VisualStudioInstallation[] installations, string? preferred, bool missing)
    {
        InitializeComponent();
        ProjectPath.Text = project;
        ProjectPath.ToolTip = project;
        if (missing) Hint.Text = Loc.Text("VisualStudioMissing") + " " + Hint.Text;
        _choices = new(installations);
        if (!string.IsNullOrEmpty(preferred) && File.Exists(preferred) &&
            !_choices.Any(item => item.ExecutablePath.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
            _choices.Add(new(Loc.Text("VisualStudioCustom"), preferred, new()));
        _choices.Add(new(Loc.Text("VisualStudioSystem"), "", new()));
        Installations.ItemsSource = _choices;
        Installations.SelectedItem = _choices.FirstOrDefault(item => item.ExecutablePath.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? _choices[0];
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = Loc.Text("VisualStudioBrowse"), Filter = "Visual Studio (devenv.exe)|devenv.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (!Path.GetFileName(picker.FileName).Equals("devenv.exe", StringComparison.OrdinalIgnoreCase)) return;
        var choice = _choices.FirstOrDefault(item => item.ExecutablePath.Equals(picker.FileName, StringComparison.OrdinalIgnoreCase));
        if (choice is null) _choices.Insert(0, choice = new(Loc.Text("VisualStudioCustom"), picker.FileName, new()));
        Installations.SelectedItem = choice;
    }

    private void OpenClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExecutable is not { } executable) return;
        if (executable.Length > 0 && !File.Exists(executable)) { Hint.Text = Loc.Text("VisualStudioMissing"); return; }
        DialogResult = true;
    }
}
