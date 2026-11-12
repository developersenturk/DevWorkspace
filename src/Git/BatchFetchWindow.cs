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
    private sealed record Result(string Repository, string Branch, string Worktree, string Status, string Detail)
    {
        public bool Included { get; set; } = true;
    }

    private readonly ObservableCollection<Result> _results;
    private readonly CancellationTokenSource _cancel = new();
    private bool _running;

    internal BatchFetchWindow(IEnumerable<ProjectEntry> repositories, CancellationToken workspaceToken)
    {
        _results = new(repositories.Select(project => new Result(project.Repository!, project.Branch,
            project.Git.Label, "Bekliyor", $"git fetch · {project.Git.RemoteNames}")));
        Title = "Toplu Fetch";
        Width = 910;
        Height = 440;
        MinWidth = 530;
        MinHeight = 260;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid { ItemsSource = _results, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false };
        var include = new DataGridCheckBoxColumn { Header = "Dahil", Binding = new Binding("Included") };
        grid.Columns.Add(include);
        void AddColumn(string header, string property, DataGridLength width) =>
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = width, IsReadOnly = true });
        AddColumn("Depo", "Repository", new(2, DataGridLengthUnitType.Star));
        AddColumn("Dal", "Branch", new(100));
        AddColumn("Çalışma ağacı", "Worktree", new(118));
        AddColumn("Durum", "Status", new(110));
        AddColumn("Komut ve sonuç", "Detail", new(2, DataGridLengthUnitType.Star));
        var start = new Button { Content = "Seçili depolarda Fetch", MinWidth = 145, Margin = new(0, 8, 8, 0) };
        var close = new Button { Content = "Kapat", MinWidth = 82, Margin = new(0, 8, 0, 0) };
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
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(start);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        content.Children.Add(buttons);
        content.Children.Add(grid);
        Content = content;
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cancel.Cancel(); } };
        Closed += (_, _) => _cancel.Dispose();
        start.Click += async (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!_results.Any(row => row.Included))
            {
                MessageBox.Show(this, "En az bir depo seçin.", Title);
                return;
            }

            grid.IsReadOnly = true;
            start.IsEnabled = false;
            close.Content = "Durdur";
            _running = true;
            try { await FetchAsync(workspaceToken); }
            finally
            {
                _running = false;
                close.Content = "Kapat";
                close.IsEnabled = true;
            }
        };
    }

    private async Task FetchAsync(CancellationToken workspaceToken)
    {
        CancellationToken token = _cancel.Token;
        for (int i = 0; i < _results.Count; i++)
        {
            var row = _results[i];
            if (!row.Included)
            {
                _results[i] = row with { Status = "Seçilmedi" };
                continue;
            }
            if (token.IsCancellationRequested)
            {
                _results[i] = row with { Status = "İptal edildi" };
                continue;
            }

            _results[i] = row with { Status = "Getiriliyor" };
            var operation = OperationCenter.Start("Toplu Fetch", row.Repository,
                () => _ = RetryFetchAsync(row.Repository, workspaceToken));
            try
            {
                var (status, detail) = await FetchRepositoryAsync(row.Repository, token);
                _results[i] = row with { Status = status, Detail = detail };
                operation.Finish(status == "Kısmen tamamlandı" ? "Başarısız" : status, detail);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _results[i] = row with { Status = "İptal edildi" };
                operation.Finish("Durduruldu");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                _results[i] = row with { Status = "Başarısız", Detail = ex.Message };
                operation.Finish("Başarısız", ex.Message);
            }
        }
    }

    private static async Task<(string Status, string Detail)> FetchRepositoryAsync(string repository, CancellationToken token)
    {
        var git = new GitWorkbench(repository);
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
        if (remotes.Length == 0)
            return ("Atlandı", "Uzak depo tanımlı değil.");
        return (failures == 0 ? "Tamamlandı" : failures == remotes.Length ? "Başarısız" : "Kısmen tamamlandı",
            string.Join(Environment.NewLine, details));
    }

    private static async Task RetryFetchAsync(string repository, CancellationToken token)
    {
        var operation = OperationCenter.Start("Fetch", repository, () => _ = RetryFetchAsync(repository, token));
        try
        {
            var (status, detail) = await FetchRepositoryAsync(repository, token);
            operation.Finish(status == "Kısmen tamamlandı" ? "Başarısız" : status, detail);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Finish("Durduruldu");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            operation.Finish("Başarısız", ex.Message);
        }
    }
}
