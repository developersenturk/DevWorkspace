using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _gitView?.IsBusy == true || _savingExplorerFolder || PackageUpdateRunning ||
            _workspaceCheck is not null || OperationCenter.Entries.Any(operation => operation.IsRunning))
        {
            MessageBox.Show(this, Loc.Text("SettingsBusy"), Loc.Text("SettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SettingsWindow(_session.Language, _session.OpenDiffOnDoubleClick) { Owner = this };
        if (dialog.ShowDialog() != true || (dialog.SelectedLanguage == _session.Language &&
            dialog.OpenDiffOnDoubleClick == _session.OpenDiffOnDoubleClick)) return;
        string previous = _session.Language;
        bool previousDoubleClick = _session.OpenDiffOnDoubleClick;
        _session.Language = dialog.SelectedLanguage;
        _session.OpenDiffOnDoubleClick = dialog.OpenDiffOnDoubleClick;
        _session.Save();
        var saved = SessionState.Load();
        if (saved.Language != dialog.SelectedLanguage || saved.OpenDiffOnDoubleClick != dialog.OpenDiffOnDoubleClick)
        {
            _session.Language = previous;
            _session.OpenDiffOnDoubleClick = previousDoubleClick;
            MessageBox.Show(this, Loc.Text("SettingsSaveFailed"), Loc.Text("SettingsTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (dialog.SelectedLanguage == previous) return;
        try
        {
            var restart = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException(Loc.Text("RestartFailed"))) { UseShellExecute = true };
            if (_workspace is { } workspace) restart.ArgumentList.Add(workspace.Root);
            // Save the current workspace and dock layout before the new process reads it.
            Close();
            if (_closing.IsCancellationRequested) Process.Start(restart);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(Loc.Text("RestartFailed") + "\n" + ex.Message, "DevWorkspace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
