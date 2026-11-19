using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DevWorkspace;

public partial class MainWindow
{
    private GitRecovery? RecoveryInventory => _session.GitRecoveryFolder is { } folder &&
        Path.IsPathFullyQualified(folder) ? GitRecovery.InFolder(folder) : null;

    private async void GitRecoveryFolderClick(object sender, RoutedEventArgs e) => await SelectRecoveryFolderAsync();

    private async Task<bool> SelectRecoveryFolderAsync()
    {
        if (_busy)
            return false;

        var picker = new OpenFolderDialog { Title = Loc.Text("LFAF73D90210C") };
        if (Directory.Exists(_session.GitRecoveryFolder))
            picker.InitialDirectory = _session.GitRecoveryFolder!;
        if (picker.ShowDialog(this) != true)
            return false;

        string folder = picker.FolderName;
        string relative = Path.GetRelativePath(GitRecovery.LegacyFolder, folder);
        if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            StatusText.Text = Loc.Text("L023CFEB7A7F9");
            return false;
        }

        var inventory = GitRecovery.InFolder(folder);
        var previous = RecoveryInventory ?? GitRecovery.InFolder(GitRecovery.LegacyFolder);
        var projects = _projects;
        var token = _closing.Token;
        try
        {
            SetBusy(true);
            await Task.Run(async () =>
            {
                inventory.AdoptFrom(previous, token);
                await inventory.RememberAsync(projects, token);
            }, token);
            token.ThrowIfCancellationRequested();
            _session.GitRecoveryFolder = folder;
            _session.Save();
            GitRecoveryFolderMenu.ToolTip = Path.Combine(folder, GitRecovery.FileName);
            StatusText.Text = Loc.Format("LE12A642BC15F", Path.Combine(folder, GitRecovery.FileName));
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusText.Text = Loc.Text("LC7DC0C42633A") + ex.Message;
            return false;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                SetBusy(false);
        }
    }

    private async void GitRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _gitView?.IsBusy == true)
            return;

        if (RecoveryInventory is null && !await SelectRecoveryFolderAsync())
            return;

        GitRecovery inventory = RecoveryInventory!;
        GitRecoveryEntry[] missing;
        try
        {
            SetBusy(true);
            missing = await Task.Run(inventory.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusText.Text = Loc.Text("L9B1064CF604F") + ex.Message;
            return;
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
                SetBusy(false);
        }

        if (_closing.IsCancellationRequested)
            return;
        var window = new GitRecoveryWindow(missing) { Owner = this };
        window.ShowDialog();
        if (window.Restored > 0)
            await RefreshAsync();
    }
}
