using System.IO;
using System.Text.Json;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private async void SaveFilterClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _savingPreference || _workspace is not { } workspace ||
            AskName("Proje filtresini kaydet", "Filtre adı") is not { } name)
            return;

        var existing = workspace.Config.SavedFilters?.FirstOrDefault(filter => filter.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && MessageBox.Show(this, $"“{name}” filtresi güncellensin mi?", "Kayıtlı filtre",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var filter = new SavedProjectFilter(existing?.Id ?? Guid.NewGuid(), name,
            CurrentProjectFilter?.Navigation ?? SelectedNavigationKey, SearchBox.Text.Trim());
        await UpdateProjectFiltersAsync(workspace, () => workspace.SaveProjectFilter(filter), filter.Key, $"Filtre kaydedildi · {name}");
    }

    private async void RemoveFilterClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _savingPreference || _workspace is not { } workspace || CurrentProjectFilter is not { } filter ||
            MessageBox.Show(this, $"“{filter.Name}” filtresi silinsin mi?", "Kayıtlı filtre",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await UpdateProjectFiltersAsync(workspace, () => workspace.RemoveProjectFilter(filter.Id), filter.Navigation, "Kayıtlı filtre silindi.");
    }

    private async Task UpdateProjectFiltersAsync(Workspace workspace, Func<Workspace> update, string key, string status)
    {
        _savingPreference = true;
        UpdateGroupSelection();
        try
        {
            var updated = await Task.Run(update);
            if (!_closing.IsCancellationRequested && string.Equals(_workspace?.Root, workspace.Root, StringComparison.OrdinalIgnoreCase))
            {
                _workspace = updated;
                BuildNavigation(key);
                StatusText.Text = status;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            if (!_closing.IsCancellationRequested)
                StatusText.Text = ex.Message;
        }
        finally
        {
            _savingPreference = false;
            if (!_closing.IsCancellationRequested)
                UpdateGroupSelection();
        }
    }
}
