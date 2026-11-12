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
            StatusText.Text = "İçe aktarmadan önce Git işleminin bitmesini bekleyin.";
            return;
        }

        var archivePicker = new OpenFileDialog
        {
            Title = "DevWorkspace çalışma alanı arşivini seçin",
            Filter = "ZIP arşivi (*.zip)|*.zip"
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

            var folderPicker = new OpenFolderDialog { Title = "Proje klasörlerinin bulunduğu çalışma alanı kökünü seçin" };
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
                string message = $"“{config.Name}” çalışma alanı şu klasöre aktarılacak:\n{root}";
                if (previous is not null)
                    message += "\n\nMevcut çalışma alanı ayarları değiştirilecek ve bir yedeği bu klasöre kaydedilecek.";
                if (hasLinks)
                    message += "\n\nArşivdeki bağlı projeler kayıtlı tam dosya yollarını kullanır. Yollar değiştiyse projeleri yeniden bağlamanız gerekir.";
                if (MessageBox.Show(this, message, "Çalışma alanını içe aktar", MessageBoxButton.YesNo,
                        MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

            await Task.Run(() => Workspace.ImportConfiguration(root, settings, previous));
            importedRoot = root;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            if (!_closing.IsCancellationRequested)
                MessageBox.Show(this, $"Çalışma alanı içe aktarılamadı.\n\n{ex.Message}",
                    "Çalışma alanını içe aktar", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            throw new InvalidDataException($"ZIP arşivinde tek bir geçerli {Workspace.ConfigFileName} bulunmalı.");

        using var entryStream = entries[0].Open();
        byte[] settings = new byte[(int)entries[0].Length];
        entryStream.ReadExactly(settings);
        if (entryStream.ReadByte() != -1)
            throw new InvalidDataException("Arşivdeki çalışma alanı ayarlarının boyutu geçersiz.");

        using var settingsStream = new MemoryStream(settings, writable: false);
        return (settings, Workspace.ReadConfiguration(settingsStream));
    }

    private async void ExportWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _workspace is not { } workspace)
            return;

        var picker = new SaveFileDialog
        {
            Title = "Çalışma alanını dışa aktar",
            FileName = "DevWorkspace.zip",
            DefaultExt = ".zip",
            Filter = "ZIP arşivi (*.zip)|*.zip"
        };
        if (picker.ShowDialog(this) != true)
            return;

        var projects = _projects;
        SetBusy(true);
        try
        {
            await Task.Run(() => WriteWorkspaceExport(picker.FileName, workspace, projects));
            StatusText.Text = $"Çalışma alanı dışa aktarıldı · {picker.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Çalışma alanı dışa aktarılamadı. {ex.Message}";
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
            "Ctrl+O   Çalışma alanı aç veya kur\nCtrl+K   Komut paleti\nCtrl+F   Projelerde ara\n" +
            "F5   Yeniden tara\nF2   Seçili gezgin klasörünü yeniden adlandır\n" +
            "Shift+F10   Seçili öğenin sağ tık menüsünü aç\nAlt+F4   DevWorkspace’i kapat",
            "Klavye kısayolları", MessageBoxButton.OK, MessageBoxImage.Information);

    private void SettingsLocationsClick(object sender, RoutedEventArgs e)
    {
        string workspaceConfig = _workspace is { } workspace
            ? Path.Combine(workspace.Root, Workspace.ConfigFileName) : "Çalışma alanı seçilmedi.";
        MessageBox.Show(this,
            $"Uygulama ayarları ve pencere düzenleri\n{SessionState.FilePath}\n\n" +
            $"Çalışma alanı ayarları\n{workspaceConfig}\n\nGit yedek klasörü\n{_session.GitRecoveryFolder ?? "Ayarlanmadı."}",
            "Ayar dosyalarının konumu", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AboutClick(object sender, RoutedEventArgs e)
    {
        string? version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        MessageBox.Show(this, $"DevWorkspace · Sürüm {version}\n\nProjeleri, çalışma alanlarını ve Git depolarını yönetmek için masaüstü uygulaması.",
            "DevWorkspace hakkında", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
