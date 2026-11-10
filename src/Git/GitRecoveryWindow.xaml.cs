using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class GitRecoveryWindow : Window
{
    private sealed record RecoveryRow(GitRecoveryEntry Entry, string Status)
    {
        public string Detail => $"{Entry.Folder}\n{Entry.Url}\n{Status}";
    }

    private bool _restoring;
    private bool _finished;
    internal int Restored { get; private set; }

    internal GitRecoveryWindow(GitRecoveryEntry[] missing)
    {
        InitializeComponent();
        Repositories.ItemsSource = missing.Select(entry => new RecoveryRow(entry, "Klasör eksik")).ToArray();
        Repositories.SelectAll();
        RecoveryStatus.Text = missing.Length == 0 ? "Kayıtlı depolar arasında eksik klasör bulunamadı." :
            "Seçimi değiştirmek için Ctrl ile satırlara tıklayın.";
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestoreButton is not null)
            RestoreButton.IsEnabled = !_restoring && !_finished && Repositories.SelectedItems.Count > 0;
    }

    private async void RestoreClick(object sender, RoutedEventArgs e)
    {
        var selected = Repositories.SelectedItems.Cast<RecoveryRow>().ToHashSet();
        var rows = ((RecoveryRow[])Repositories.ItemsSource).ToArray();
        _restoring = true;
        RestoreButton.IsEnabled = CloseButton.IsEnabled = false;
        try
        {
            for (int index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (!selected.Contains(row))
                    continue;
                RecoveryStatus.Text = $"Klonlanıyor… {Path.GetFileName(row.Entry.Folder)}";
                try
                {
                    await Task.Run(() => GitRecovery.RestoreAsync(row.Entry, CancellationToken.None));
                    rows[index] = row with { Status = "Geri yüklendi" };
                    Restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException)
                {
                    rows[index] = row with { Status = ex.Message };
                }
                Repositories.ItemsSource = rows.ToArray();
            }
            _finished = true;
            RecoveryStatus.Text = $"{Restored} depo geri yüklendi. {selected.Count - Restored} depo geri yüklenemedi.";
        }
        finally
        {
            _restoring = false;
            CloseButton.IsEnabled = true;
        }
    }

    private void WindowClosing(object? sender, CancelEventArgs e) => e.Cancel = _restoring;
}
