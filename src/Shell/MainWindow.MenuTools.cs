using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace DevWorkspace;

public partial class MainWindow
{
    private async void ImportWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _savingExplorerFolder || _savingPreference)
            return;
        if (_gitView?.IsBusy == true)
        {
            StatusText.Text = Loc.Text("L79C58A20A547");
            return;
        }

        var archivePicker = new OpenFileDialog
        {
            Title = Loc.Text("LB0092CD5B4CF"),
            Filter = Loc.Text("L9FFC9D4F5903")
        };
        if (archivePicker.ShowDialog(this) != true)
            return;

        string? importedRoot = null;
        SetBusy(true);
        try
        {
            var (settings, config) = await Task.Run(() => ReadWorkspaceImport(archivePicker.FileName));
            if (_closing.IsCancellationRequested)
                return;

            var folderPicker = new OpenFolderDialog { Title = Loc.Text("L25E0EF5574AC") };
            if (_workspace is { } workspace)
                folderPicker.InitialDirectory = workspace.Root;
            if (folderPicker.ShowDialog(this) != true)
                return;

            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPicker.FolderName));
            string configPath = Path.Combine(root, Workspace.ConfigFileName);
            byte[]? previous = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
            bool hasLinks = config.LinkedProjects?.Length > 0;
            if (previous is not null || hasLinks)
            {
                string message = Loc.Format("LCF3102ECBB62", config.Name, root);
                if (previous is not null)
                    message += Loc.Text("LBD668CA82A4D");
                if (hasLinks)
                    message += Loc.Text("L6EF4058ECEE2");
                if (MessageBox.Show(this, message, Loc.Text("L10B03713F153"), MessageBoxButton.YesNo,
                        MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

            await Task.Run(() => Workspace.ImportConfiguration(root, settings, previous));
            importedRoot = root;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            if (!_closing.IsCancellationRequested)
                MessageBox.Show(this, Loc.Format("LAD4A1CEE4015", ex.Message),
                    Loc.Text("L10B03713F153"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
                SetBusy(false);
        }

        if (importedRoot is not null && !_closing.IsCancellationRequested)
            await OpenWorkspaceAsync(importedRoot);
    }

    private static (byte[] Settings, WorkspaceConfig Config) ReadWorkspaceImport(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(entry =>
            entry.FullName.Equals(Workspace.ConfigFileName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 or > 8 * 1024 * 1024)
            throw new InvalidDataException(Loc.Format("LA6665B5F2EE0", Workspace.ConfigFileName));

        using var entryStream = entries[0].Open();
        byte[] settings = new byte[(int)entries[0].Length];
        entryStream.ReadExactly(settings);
        if (entryStream.ReadByte() != -1)
            throw new InvalidDataException(Loc.Text("L039415E15399"));

        using var settingsStream = new MemoryStream(settings, writable: false);
        return (settings, Workspace.ReadConfiguration(settingsStream));
    }

    private async void ExportWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _workspace is not { } workspace)
            return;

        var picker = new SaveFileDialog
        {
            Title = Loc.Text("LA87EE6811416"),
            FileName = "DevWorkspace.zip",
            DefaultExt = ".zip",
            Filter = Loc.Text("L9FFC9D4F5903")
        };
        if (picker.ShowDialog(this) != true)
            return;

        var projects = _projects;
        SetBusy(true);
        try
        {
            await Task.Run(() => WriteWorkspaceExport(picker.FileName, workspace, projects));
            StatusText.Text = Loc.Format("L5A081A0A5F3B", picker.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.Format("LCA2B63C1E986", ex.Message);
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
                SetBusy(false);
        }
    }

    private static void WriteWorkspaceExport(string destination, Workspace workspace, IReadOnlyList<ProjectEntry> projects)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                using (var configuration = archive.CreateEntry(Workspace.ConfigFileName).Open())
                    workspace.WriteConfiguration(configuration);
                using var inventory = archive.CreateEntry("projeler.json").Open();
                JsonSerializer.Serialize(inventory, projects.Select(project => new
                {
                    project.Name,
                    project.RelativePath,
                    project.Kind,
                    project.Preference.Group,
                    project.Preference.Label,
                    project.Preference.Favorite
                }), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void KeyboardShortcutsClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            Loc.Text("LF78AEB3FA049") +
            Loc.Text("L2F260A895F20") +
            Loc.Text("DiffKeyboardShortcuts") +
            Loc.Text("L6427622828EF"),
            Loc.Text("LDAE232AB03CD"), MessageBoxButton.OK, MessageBoxImage.Information);

    private void SettingsLocationsClick(object sender, RoutedEventArgs e)
    {
        string workspaceConfig = _workspace is { } workspace
            ? Path.Combine(workspace.Root, Workspace.ConfigFileName) : Loc.Text("L9DD744583D09");
        MessageBox.Show(this,
            Loc.Format("LF2FB5A4AFADC", SessionState.FilePath) +
            Loc.Format("L4ABE4BFA2AB8", workspaceConfig, _session.GitRecoveryFolder ?? Loc.Text("LB254C96B00BF")),
            Loc.Text("L1D400DEC5BFB"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AboutClick(object sender, RoutedEventArgs e)
    {
        string? version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        MessageBox.Show(this, Loc.Format("L791FBAD5F87F", version),
            Loc.Text("LDBC6A81F6489"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
