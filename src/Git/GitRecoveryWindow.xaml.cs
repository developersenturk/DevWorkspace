using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class GitRecoveryWindow : ThemedWindow
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
        Repositories.ItemsSource = missing.Select(entry => new RecoveryRow(entry, Loc.Text("L4620345397EF"))).ToArray();
        Repositories.SelectAll();
        RecoveryStatus.Text = missing.Length == 0 ? Loc.Text("L4D97FAED8152") :
            Loc.Text("L94E08840916C");
    }

    private void SelectionChanged(object sender, RoutedEventArgs e)
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
                RecoveryStatus.Text = Loc.Format("LDE8993D1230F", Path.GetFileName(row.Entry.Folder));
                try
                {
                    await Task.Run(() => GitRecovery.RestoreAsync(row.Entry, CancellationToken.None));
                    rows[index] = row with { Status = Loc.Text("L9C445D9C1402") };
                    Restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException)
                {
                    rows[index] = row with { Status = ex.Message };
                }
                Repositories.ItemsSource = rows.ToArray();
            }
            _finished = true;
            RecoveryStatus.Text = Loc.Format("L05422351D607", Restored, selected.Count - Restored);
        }
        finally
        {
            _restoring = false;
            CloseButton.IsEnabled = true;
        }
    }

    private void WindowClosing(object? sender, CancelEventArgs e) => e.Cancel = _restoring;
}
