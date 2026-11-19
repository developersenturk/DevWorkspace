using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Grid;
using Microsoft.Win32;

namespace DevWorkspace;

internal sealed class GitWorktreeWindow : ThemedWindow
{
    private sealed record Worktree(string Path, string Branch, string Commit, string Status, bool Main);

    private readonly string _repository;
    private readonly Func<string, bool> _openFolder;
    private readonly CancellationTokenSource _cancel = new();
    private readonly GridControl _trees = WorkbenchGrid.Create(Loc.Text("L3D1AF3569EB3"));
    private readonly ComboBox _branches = new() { MinWidth = 185, Margin = new(0, 0, 8, 0) };
    private readonly TextBox _newBranch = new() { MinWidth = 125, Margin = new(0, 0, 8, 0), ToolTip = Loc.Text("LA8A008D78F9E") };
    private readonly StackPanel _commands = new() { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private bool _running;

    internal GitWorktreeWindow(string repository, Func<string, bool> openFolder)
    {
        _repository = repository;
        _openFolder = openFolder;
        Title = Loc.Text("L3D1AF3569EB3");
        Width = 870;
        Height = 460;
        MinWidth = 560;
        MinHeight = 290;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var (header, property) in new[] { (Loc.Text("L1DF9A7B4EB23"), "Path"), (Loc.Text("L1245EEBD439F"), "Branch"), ("Commit", "Commit"), (Loc.Text("L8BD057388E28"), "Status") })
            _trees.Columns.Add(WorkbenchGrid.Column(header, property, new(property == "Path" ? 3 : 1, GridColumnUnitType.Star)));
        _commands.Children.Add(_branches);
        System.Windows.Automation.AutomationProperties.SetName(_newBranch, Loc.Text("L633F4273E5E4"));
        _commands.Children.Add(_newBranch);
        AddCommand(Loc.Text("L04EE593941D9"), AddAsync);
        AddCommand(Loc.Text("LD6F2F6CAD078"), () =>
        {
            if (_trees.CurrentItem is Worktree worktree)
                _openFolder(worktree.Path);
            return Task.CompletedTask;
        });
        AddCommand(Loc.Text("L280446943DF7"), RemoveAsync);
        AddCommand(Loc.Text("L63CD725787A8"), LoadAsync);

        var footer = new StackPanel();
        footer.Children.Add(_commands);
        footer.Children.Add(_status);
        var panel = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        panel.Children.Add(_trees);
        Content = panel;
        Loaded += async (_, _) => await RunAsync(LoadAsync);
        Closing += (_, e) => e.Cancel = _running;
        Closed += (_, _) => _cancel.Dispose();
    }

    private void AddCommand(string label, Func<Task> command)
    {
        var button = new Button { Content = label, Padding = new(8, 3, 8, 3), Margin = new(0, 0, 8, 0) };
        button.Click += async (_, _) => await RunAsync(command);
        _commands.Children.Add(button);
    }

    private async Task RunAsync(Func<Task> command)
    {
        if (_running)
            return;
        _running = true;
        _commands.IsEnabled = false;
        try { await command(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            _running = false;
            _commands.IsEnabled = true;
        }
    }

    private async Task<string> GitAsync(params string[] arguments)
    {
        var (code, output, error) = await GitProjectOperations.RunAsync(_repository, arguments, _cancel.Token, timeoutSeconds: 120);
        if (code != 0)
            throw new IOException(string.IsNullOrWhiteSpace(error) ? output : error.Trim());
        return output;
    }

    private async Task LoadAsync()
    {
        var trees = Parse(await GitAsync("worktree", "list", "--porcelain", "-z"));
        var branches = (await GitAsync("for-each-ref", "--format=%(refname:short)", "refs/heads"))
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        _trees.ItemsSource = trees;
        _branches.ItemsSource = branches.Except(trees.Select(tree => tree.Branch), StringComparer.Ordinal).ToArray();
        _branches.SelectedIndex = 0;
        _status.Text = Loc.Format("L1705DD306412", trees.Length);
    }

    private async Task AddAsync()
    {
        string? branch = _branches.SelectedItem as string;
        string newBranch = _newBranch.Text.Trim();
        if (newBranch.Length > 0)
            await GitAsync("check-ref-format", "--branch", newBranch);
        else if (branch is null)
        {
            _status.Text = Loc.Text("LC0C3CDA9EB16");
            return;
        }
        var picker = new OpenFolderDialog { Title = Loc.Text("LB0AD1221E9D6") };
        if (picker.ShowDialog(this) != true)
            return;
        if (Directory.EnumerateFileSystemEntries(picker.FolderName).Any())
        {
            _status.Text = Loc.Text("L87DD68902E9A");
            return;
        }
        if (newBranch.Length > 0)
        {
            await GitAsync("worktree", "add", "-b", newBranch, "--", picker.FolderName, "HEAD");
            _newBranch.Clear();
        }
        else
            await GitAsync("worktree", "add", "--", picker.FolderName, branch!);
        await LoadAsync();
    }

    private async Task RemoveAsync()
    {
        if (_trees.CurrentItem is not Worktree worktree || worktree.Main ||
            string.Equals(worktree.Path.Replace('/', '\\'), _repository, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = Loc.Text("L063E9B349E9B");
            return;
        }
        if (MessageBox.Show(this, Loc.Format("LB0260DD14960", worktree.Path), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await GitAsync("worktree", "remove", "--", worktree.Path);
        await LoadAsync();
    }

    private static Worktree[] Parse(string output)
    {
        return output.Split("\0\0", StringSplitOptions.RemoveEmptyEntries).Select((record, index) =>
        {
            string[] fields = record.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            string Value(string prefix) => fields.FirstOrDefault(field => field.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? "";
            string commit = Value("HEAD ");
            string status = fields.Any(field => field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal))
                ? Loc.Text("L3F0A3653AE48") : fields.Any(field => field.StartsWith("prunable", StringComparison.Ordinal)) ? Loc.Text("LE8AF1BA5B6FF") : Loc.Text("L970DBBD01996");
            string branch = Value("branch refs/heads/");
            return new Worktree(Value("worktree "), branch.Length > 0 ? branch : "—", commit[..Math.Min(commit.Length, 8)], status, index == 0);
        }).Where(tree => tree.Path.Length > 0).ToArray();
    }
}
