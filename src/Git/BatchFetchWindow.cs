using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Editors.Settings;
using DevExpress.Xpf.Grid;

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
            project.Git.Label, Loc.Text("L9025B4AF025A"), $"git fetch · {project.Git.RemoteNames}")));
        Title = Loc.Text("L04777757E3AE");
        Width = 1100;
        Height = 440;
        MinWidth = 530;
        MinHeight = 260;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = WorkbenchGrid.Create(Title);
        grid.ItemsSource = _results;
        var view = (TableView)grid.View;
        view.AllowEditing = true;
        view.NavigationStyle = GridViewNavigationStyle.Cell;
        var include = new GridColumn { Header = Loc.Text("L2A957CDC6892"), FieldName = "Included", Width = 65, MinWidth = 65, MaxWidth = 65, EditSettings = new CheckEditSettings() };
        grid.Columns.Add(include);
        void AddColumn(string header, string property, GridColumnWidth width) =>
            grid.Columns.Add(WorkbenchGrid.Column(header, property, width));
        AddColumn(Loc.Text("L582095FBE4C3"), "Repository", new(2, GridColumnUnitType.Star));
        AddColumn(Loc.Text("L1245EEBD439F"), "Branch", new(100));
        AddColumn(Loc.Text("L55998A2BD3B4"), "Worktree", new(118));
        AddColumn(Loc.Text("L8BD057388E28"), "Status", new(110));
        AddColumn(Loc.Text("L2B36A0FE599A"), "Detail", new(2, GridColumnUnitType.Star));
        grid.Columns[1].MinWidth = 260;
        grid.Columns[5].MinWidth = 220;
        var start = new Button { Content = Loc.Text("L6F312B82B8DF"), MinWidth = 145, Margin = new(0, 8, 8, 0) };
        var close = new Button { Content = Loc.Text("L7B31A9FC4816"), MinWidth = 82, Margin = new(0, 8, 0, 0) };
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
            if (!view.CommitEditing()) return;
            if (!_results.Any(row => row.Included))
            {
                MessageBox.Show(this, Loc.Text("LD1152FC0382B"), Title);
                return;
            }

            view.AllowEditing = false;
            start.IsEnabled = false;
            close.Content = Loc.Text("L5769A8F8AECD");
            _running = true;
            try { await FetchAsync(workspaceToken); }
            finally
            {
                _running = false;
                close.Content = Loc.Text("L7B31A9FC4816");
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
                _results[i] = row with { Status = Loc.Text("LE6DDB798B890") };
                continue;
            }
            if (token.IsCancellationRequested)
            {
                _results[i] = row with { Status = Loc.Text("LDBD417DA03E1") };
                continue;
            }

            _results[i] = row with { Status = Loc.Text("L922579148843") };
            var operation = OperationCenter.Start(Loc.Text("L04777757E3AE"), row.Repository,
                () => _ = RetryFetchAsync(row.Repository, workspaceToken));
            try
            {
                var (status, detail) = await FetchRepositoryAsync(row.Repository, token);
                _results[i] = row with { Status = status, Detail = detail };
                operation.Finish(status == Loc.Text("L34B5B124A137") ? Loc.Text("LF0873175A881") : status, detail);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _results[i] = row with { Status = Loc.Text("LDBD417DA03E1") };
                operation.Finish(Loc.Text("L7C925A8624F1"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                _results[i] = row with { Status = Loc.Text("LF0873175A881"), Detail = ex.Message };
                operation.Finish(Loc.Text("LF0873175A881"), ex.Message);
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
                details.Add(Loc.Format("L4905B289A951", remote));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                failures++;
                details.Add($"{remote} · {ex.Message}");
            }
        }
        if (remotes.Length == 0)
            return (Loc.Text("L594B1254C47D"), Loc.Text("LF8A40EEE03A3"));
        return (failures == 0 ? Loc.Text("L7C6F3F5F37BC") : failures == remotes.Length ? Loc.Text("LF0873175A881") : Loc.Text("L34B5B124A137"),
            string.Join(Environment.NewLine, details));
    }

    private static async Task RetryFetchAsync(string repository, CancellationToken token)
    {
        var operation = OperationCenter.Start("Fetch", repository, () => _ = RetryFetchAsync(repository, token));
        try
        {
            var (status, detail) = await FetchRepositoryAsync(repository, token);
            operation.Finish(status == Loc.Text("L34B5B124A137") ? Loc.Text("LF0873175A881") : status, detail);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.Finish(Loc.Text("L7C925A8624F1"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            operation.Finish(Loc.Text("LF0873175A881"), ex.Message);
        }
    }
}
