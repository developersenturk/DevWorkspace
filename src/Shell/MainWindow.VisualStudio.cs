using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private bool _openingVisualStudio;

    private async Task<bool> OpenVisualStudioAsync(string path, bool choose = false)
    {
        if (_openingVisualStudio || _closing.IsCancellationRequested) return false;
        _openingVisualStudio = true;
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException(Loc.Format("L339D8FF997E5", path));
            bool remembered = _session.VisualStudioPreferences.TryGetValue(path, out string? executable);
            bool missing = remembered && (executable is null || executable.Length > 0 &&
                (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)));
            if (choose || !remembered || missing)
            {
                StatusText.Text = Loc.Text("VisualStudioSearching");
                var installations = await VisualStudioInstallations.DiscoverAsync(_closing.Token);
                if (_closing.IsCancellationRequested) return false;
                if (!choose && !remembered && installations.Length == 1)
                    executable = installations[0].ExecutablePath;
                else
                {
                    var picker = new VisualStudioPickerWindow(path, installations, executable, missing) { Owner = this };
                    if (picker.ShowDialog() != true) { StatusText.Text = Loc.Text("VisualStudioCanceled"); return false; }
                    executable = picker.SelectedExecutable;
                }
            }
            if (executable is null) return false;
            bool opened;
            if (executable.Length == 0) opened = OpenPath(path, offerEditor: true);
            else
            {
                if (!File.Exists(executable)) throw new FileNotFoundException(Loc.Text("VisualStudioMissing"));
                opened = Process.Start(VisualStudioInstallations.StartInfo(executable, path)) is not null;
            }
            if (!opened) return false;
            _session.VisualStudioPreferences[path] = executable;
            bool saved = _session.SaveTo(SessionState.FilePath);
            StatusText.Text = Loc.Text(saved ? "VisualStudioOpened" : "VisualStudioSaveFailed");
            return true;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = ex.Message;
            return false;
        }
        finally { _openingVisualStudio = false; }
    }

    private async void ChangeProjectVisualStudio(ProjectEntry project)
    {
        if (_busy || !VisualStudioInstallations.Supports(project.EntryFile)) return;
        string? root = _workspace?.Root;
        if (await OpenVisualStudioAsync(project.EntryFile!, choose: true) && _workspace?.Root == root)
        {
            if (_workspace is { } workspace && IsSolution(project.EntryFile!)) _session.RememberSolution(workspace.Root, project.EntryFile!);
            RememberOpened(project);
        }
    }

    private void DrawerVisualStudioClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { } project) ChangeProjectVisualStudio(project);
    }
}
