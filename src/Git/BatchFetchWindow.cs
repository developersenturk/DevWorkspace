using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal sealed class BatchFetchWindow : ThemedWindow
{
    private sealed record Result(string Repository, string Status, string Detail = "");

    private readonly ObservableCollection<Result> _results;
    private readonly CancellationTokenSource _cancel = new();
    private bool _running;

    internal BatchFetchWindow(IEnumerable<string> repositories)
    {
        _results = new(repositories.Select(repository => new Result(repository, "Bekliyor")));
        Title = "Toplu Fetch";
        Width = 910;
        Height = 440;
        MinWidth = 530;
        MinHeight = 260;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid { ItemsSource = _results, IsReadOnly = true, AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Depo", Binding = new Binding("Repository"), Width = new(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Durum", Binding = new Binding("Status"), Width = new(110) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ayrıntı", Binding = new Binding("Detail"), Width = new(3, DataGridLengthUnitType.Star) });
        var close = new Button { Content = "Durdur", MinWidth = 82, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        close.Click += (_, _) =>
        {
            if (_running)
            {
                _cancel.Cancel();
                close.IsEnabled = false;
            }
            else
                Close();
        };
        var content = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(close, Dock.Bottom);
        content.Children.Add(close);
        content.Children.Add(grid);
        Content = content;
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cancel.Cancel(); } };
        Closed += (_, _) => _cancel.Dispose();
        Loaded += async (_, _) =>
        {
            _running = true;
            try { await FetchAsync(); }
            finally
            {
                _running = false;
                close.Content = "Kapat";
                close.IsEnabled = true;
            }
        };
    }

    private async Task FetchAsync()
    {
        CancellationToken token = _cancel.Token;
        for (int i = 0; i < _results.Count; i++)
        {
            var row = _results[i];
            if (token.IsCancellationRequested)
            {
                _results[i] = row with { Status = "İptal edildi" };
                continue;
            }

            _results[i] = row with { Status = "Getiriliyor" };
            try
            {
                var git = new GitWorkbench(row.Repository);
                string[] remotes = await git.ReadRemoteNamesAsync(token);
                var details = new List<string>();
                int failures = 0;
                foreach (string remote in remotes)
                {
                    try
                    {
                        await git.FetchAsync(remote, token);
                        details.Add($"{remote} · Tamamlandı");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                    {
                        failures++;
                        details.Add($"{remote} · {ex.Message}");
                    }
                }
                _results[i] = row with
                {
                    Status = remotes.Length == 0 ? "Atlandı" : failures == 0 ? "Tamamlandı" :
                        failures == remotes.Length ? "Başarısız" : "Kısmen tamamlandı",
                    Detail = remotes.Length == 0 ? "Uzak depo tanımlı değil." : string.Join(Environment.NewLine, details)
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _results[i] = row with { Status = "İptal edildi" };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                _results[i] = row with { Status = "Başarısız", Detail = ex.Message };
            }
        }
    }
}
