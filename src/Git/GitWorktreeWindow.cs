using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DevExpress.Xpf.Core;
using Microsoft.Win32;

namespace DevWorkspace;

internal sealed class GitWorktreeWindow : ThemedWindow
{
    private sealed record Worktree(string Path, string Branch, string Commit, string Status, bool Main);

    private readonly string _repository;
    private readonly Func<string, bool> _openFolder;
    private readonly CancellationTokenSource _cancel = new();
    private readonly DataGrid _trees = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
    private readonly ComboBox _branches = new() { MinWidth = 185, Margin = new(0, 0, 8, 0) };
    private readonly TextBox _newBranch = new() { MinWidth = 125, Margin = new(0, 0, 8, 0), ToolTip = "HEAD’den yeni dal adı (isteğe bağlı)" };
    private readonly StackPanel _commands = new() { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private bool _running;

    internal GitWorktreeWindow(string repository, Func<string, bool> openFolder)
    {
        _repository = repository;
        _openFolder = openFolder;
        Title = "Dallar ve worktree’ler";
        Width = 870;
        Height = 460;
        MinWidth = 560;
        MinHeight = 290;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        foreach (var (header, property) in new[] { ("Klasör", "Path"), ("Dal", "Branch"), ("Commit", "Commit"), ("Durum", "Status") })
            _trees.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = new(property == "Path" ? 3 : 1, DataGridLengthUnitType.Star) });
        _commands.Children.Add(_branches);
        System.Windows.Automation.AutomationProperties.SetName(_newBranch, "HEAD’den yeni dal adı");
        _commands.Children.Add(_newBranch);
        AddCommand("Worktree aç…", AddAsync);
        AddCommand("Klasörü aç", () =>
        {
            if (_trees.SelectedItem is Worktree worktree)
                _openFolder(worktree.Path);
            return Task.CompletedTask;
        });
        AddCommand("Worktree kaldır…", RemoveAsync);
        AddCommand("Yenile", LoadAsync);

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
        _status.Text = $"{trees.Length:N0} çalışma kopyası";
    }

    private async Task AddAsync()
    {
        string? branch = _branches.SelectedItem as string;
        string newBranch = _newBranch.Text.Trim();
        if (newBranch.Length > 0)
            await GitAsync("check-ref-format", "--branch", newBranch);
        else if (branch is null)
        {
            _status.Text = "Başka bir çalışma kopyasında açık olmayan yerel bir dal seçin.";
            return;
        }
        var picker = new OpenFolderDialog { Title = "Yeni worktree için boş bir klasör seçin" };
        if (picker.ShowDialog(this) != true)
            return;
        if (Directory.EnumerateFileSystemEntries(picker.FolderName).Any())
        {
            _status.Text = "Seçilen klasör boş olmalı.";
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
        if (_trees.SelectedItem is not Worktree worktree || worktree.Main ||
            string.Equals(worktree.Path.Replace('/', '\\'), _repository, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Kaldırmak için açık depo dışında bir çalışma kopyası seçin.";
            return;
        }
        if (MessageBox.Show(this, $"{worktree.Path} çalışma kopyası kaldırılsın mı?", Title,
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
                ? "Kilitli" : fields.Any(field => field.StartsWith("prunable", StringComparison.Ordinal)) ? "Eksik" : "Hazır";
            string branch = Value("branch refs/heads/");
            return new Worktree(Value("worktree "), branch.Length > 0 ? branch : "—", commit[..Math.Min(commit.Length, 8)], status, index == 0);
        }).Where(tree => tree.Path.Length > 0).ToArray();
    }
}
