using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevExpress.Xpf.Core;
using Microsoft.Win32;

namespace DevWorkspace;

public partial class GitHubRepositoriesWindow : ThemedWindow
{
    private sealed record OwnerChoice(string? Login, string Label, string Kind, int Count);

    private readonly string? _workspaceRoot;
    private readonly string? _remoteProject;
    private GitHubRepository[] _repositories = [];
    private string? _account;
    private CancellationTokenSource? _operation;

    internal string? ClonedFolder { get; private set; }
    internal GitHubRepository? SelectedRemote { get; private set; }

    internal GitHubRepositoriesWindow(string? workspaceRoot, string? remoteProject = null)
    {
        _workspaceRoot = workspaceRoot;
        _remoteProject = remoteProject;
        InitializeComponent();
        if (_remoteProject is not null)
        {
            LinkClonePanel.Visibility = Visibility.Collapsed;
            Title = Loc.Text("L4C0A60283194");
            CloneButton.Content = Loc.Text("LF28687C884B1");
            WritableOnly.IsChecked = true;
        }
        else if (workspaceRoot is null)
        {
            CloneButton.ToolTip = Loc.Text("LCC44503E4B6E");
            CloneUrlButton.ToolTip = CloneButton.ToolTip;
        }
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private Task RefreshAsync() => RunAsync(Loc.Text("L93196E43B035"), ReadRepositoriesAsync);

    private async Task ReadRepositoriesAsync(CancellationToken token)
    {
        string? previousOwner = (Owners.SelectedItem as OwnerChoice)?.Login;
        string? previousRepository = (Repositories.CurrentItem as GitHubRepository)?.FullName;
        _account = null;
        Account.Text = Loc.Text("L9CD6624AD1AB");
        _repositories = [];
        Owners.ItemsSource = null;
        Repositories.ItemsSource = _repositories;
        EmptyResults.Visibility = Visibility.Collapsed;
        string account = await GitHubCli.ReadAccountAsync(token);
        if (string.IsNullOrWhiteSpace(account))
            throw new IOException(Loc.Text("LA79F9B01442B"));
        _repositories = await GitHubCli.ReadRepositoriesAsync(token);
        var groups = _repositories.GroupBy(repo => repo.Owner, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                bool ownAccount = string.Equals(group.Key, account, StringComparison.OrdinalIgnoreCase);
                string kind = ownAccount ? Loc.Text("L8CE1BD6C15BB") : group.First().IsOrganization ? Loc.Text("L0F43367C2950") : Loc.Text("L2837FCF3B2F5");
                return new OwnerChoice(group.Key, group.Key, kind, group.Count());
            })
            .OrderByDescending(owner => string.Equals(owner.Login, account, StringComparison.OrdinalIgnoreCase))
            .ThenBy(owner => owner.Label, StringComparer.OrdinalIgnoreCase);
        OwnerChoice[] owners = [new(null, Loc.Text("LA570FC049A49"), Loc.Text("L2004308435DC"), _repositories.Length), .. groups];
        Owners.ItemsSource = owners;
        Owners.SelectedItem = owners.FirstOrDefault(owner => string.Equals(owner.Login, previousOwner, StringComparison.OrdinalIgnoreCase))
            ?? owners[0];
        _account = account;
        Account.Text = Loc.Format("L550B2151EB19", account, _repositories.Length);
        ApplySearch(previousRepository);
    }

    private async void InstallClick(object sender, RoutedEventArgs e) =>
        await RunAsync("GitHub CLI kuruluyor…", async token =>
        {
            await GitHubCli.InstallAsync(token);
            await ReadRepositoriesAsync(token);
        });

    private async void LoginClick(object sender, RoutedEventArgs e) =>
        await RunAsync(Loc.Text("L332073CC27FA"), async token =>
        {
            var progress = new Progress<string>(code =>
            {
                if (_operation?.Token != token || token.IsCancellationRequested)
                    return;
                DeviceCode.Text = code;
                DevicePanel.Visibility = Visibility.Visible;
                Status.Text = Loc.Text("LA5FC7022AE90");
            });
            try { await GitHubCli.LoginAsync(progress, token); }
            finally
            {
                DeviceCode.Clear();
                DevicePanel.Visibility = Visibility.Collapsed;
            }
            Status.Text = Loc.Text("L93196E43B035");
            await ReadRepositoriesAsync(token);
        });

    private async void CloneClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _account is not string account ||
            Repositories.CurrentItem is not GitHubRepository repository)
            return;

        if (_remoteProject is not null)
        {
            SelectedRemote = repository;
            DialogResult = true;
            return;
        }
        if (_workspaceRoot is null)
            return;

        await CloneAsync(account, repository);
    }

    private void RepositoryUrlChanged(object sender, TextChangedEventArgs e) => UpdateActions();

    private async void RepositoryUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await CloneFromUrlAsync();
    }

    private async void CloneUrlClick(object sender, RoutedEventArgs e) => await CloneFromUrlAsync();

    private async Task CloneFromUrlAsync()
    {
        if (_operation is not null || _workspaceRoot is null || string.IsNullOrWhiteSpace(RepositoryUrl.Text))
            return;
        string url = RepositoryUrl.Text;
        GitHubRepository? repository = null;
        string? account = null;
        await RunAsync(Loc.Text("LB4051CFB9D9E"), async token =>
        {
            // Validate locally before requesting credentials or accessing the network.
            GitHubCli.RepositoryNameFromUrl(url);
            account = await GitHubCli.ReadAccountAsync(token);
            repository = await GitHubCli.ReadRepositoryByUrlAsync(url, token);
        });
        if (repository is not null && account is not null)
            await CloneAsync(account, repository);
        else
            RepositoryUrl.Focus();
    }

    private async Task CloneAsync(string account, GitHubRepository repository)
    {
        var picker = new OpenFolderDialog
        {
            Title = Loc.Format("L10B3533B0F8B", repository.FullName),
            InitialDirectory = _workspaceRoot
        };
        if (picker.ShowDialog(this) != true)
            return;

        await RunAsync(Loc.Format("L08FEC4CB33B9", repository.FullName), async token =>
        {
            ClonedFolder = await GitHubCli.CloneRepositoryAsync(account, repository, picker.FolderName, token);
        });
        if (ClonedFolder is not null)
            DialogResult = true;
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action, TextBlock? feedback = null)
    {
        if (_operation is not null)
            return;

        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        feedback ??= Status;
        InstallButton.Visibility = LoginButton.Visibility = Visibility.Collapsed;
        feedback.Text = message;
        UpdateActions();
        try { await action(cancellation.Token); }
        catch (GitHubCliMissingException ex)
        {
            feedback.Text = ex.Message;
            InstallButton.Visibility = Visibility.Visible;
        }
        catch (GitHubLoginRequiredException ex)
        {
            _account = null;
            _repositories = [];
            Owners.ItemsSource = null;
            Repositories.ItemsSource = _repositories;
            Account.Text = Loc.Text("L9CD6624AD1AB");
            Status.Text = ex.Message;
            feedback.Text = ex.Message;
            LoginButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            feedback.Text = Loc.Text("L294EA0BB2D0A");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or JsonException)
        {
            feedback.Text = ex.Message;
        }
        finally
        {
            _operation = null;
            UpdateActions();
        }
    }

    private void SearchChanged(object sender, RoutedEventArgs e) => ApplySearch();

    private void ApplySearch(string? previousRepository = null)
    {
        if (_account is null)
            return;

        var owner = Owners.SelectedItem as OwnerChoice;
        previousRepository ??= (Repositories.CurrentItem as GitHubRepository)?.FullName;
        string[] terms = Search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        GitHubRepository[] visible = _repositories
            .Where(repo => (owner?.Login is null || string.Equals(owner.Login, repo.Owner, StringComparison.OrdinalIgnoreCase)) &&
                (WritableOnly.IsChecked != true || repo is { Archived: false, CanPush: true }) &&
                terms.All(term => MatchesTerm(repo, term)))
            .OrderByDescending(repo => _remoteProject is not null && repo.Name.Equals(_remoteProject, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ListHeading.Text = owner?.Label ?? Loc.Text("LA570FC049A49");
        OwnerColumn.Visible = owner?.Login is null;
        Repositories.ItemsSource = visible;
        Repositories.CurrentItem = visible.FirstOrDefault(repo => repo.FullName.Equals(previousRepository, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault();
        EmptyResults.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = Loc.Format("L2276FB23899D", visible.Length, owner?.Count ?? _repositories.Length);
        if (_workspaceRoot is null)
            Status.Text += Loc.Text("LEED5DEB60C5D");
        UpdateActions();
    }

    private static bool MatchesTerm(GitHubRepository repo, string term)
    {
        bool Contains(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
        return Contains(repo.FullName) || Contains(repo.Description) || Contains(repo.Language) || (repo.Topics ?? []).Any(Contains);
    }

    private void RepositoryChanged(object sender, RoutedEventArgs e)
    {
        if (CloneButton is null)
            return;

        var repo = Repositories.CurrentItem as GitHubRepository;
        DetailEmpty.Visibility = repo is null ? Visibility.Visible : Visibility.Collapsed;
        RepositoryDetails.Visibility = repo is null ? Visibility.Collapsed : Visibility.Visible;
        RepositoryDetails.ScrollToTop();
        if (repo is not null)
        {
            DetailName.Text = repo.Name;
            DetailOwner.Text = $"{repo.Owner} · {(repo.IsOrganization ? Loc.Text("L0F43367C2950") : Loc.Text("L2837FCF3B2F5"))}";
            DetailDescription.Text = repo.DescriptionLabel + (string.IsNullOrWhiteSpace(repo.Description) ? ". README’den inceleyebilirsiniz." : "");
            DetailAccess.Text = $"{repo.VisibilityLabel} · {repo.AccessLabel}";
            DetailTechnology.Text = Loc.Format("L698D77CC788F", repo.Language ?? Loc.Text("L6317EAC1D512"), repo.DefaultBranch ?? "—");
            DetailActivity.Text = repo.PushedAt is { Year: > 1 } pushed
                ? Loc.Format("LFC0CC52A1D2B", pushed.ToLocalTime()) : Loc.Text("L6BF466220B5A");
            DetailTopics.Text = string.Join(" · ", repo.Topics ?? []);
        }
        UpdateActions();
    }

    private async void ReadmeClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || Repositories.CurrentItem is not GitHubRepository repo)
            return;

        ReadmeRepository.Text = repo.FullName;
        RepositoryBrowser.Visibility = Visibility.Collapsed;
        ReadmeView.Visibility = Visibility.Visible;
        ReadmeBackButton.Focus();
        await RunAsync(Loc.Text("L02C90C7BC595"), async token =>
        {
            string readme = await GitHubCli.ReadRepositoryReadmeAsync(repo, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(readme))
            {
                ReadmeStatus.Text = Loc.Text("LEE889703153C");
                return;
            }

            ReadmeReader.Document = DocumentPreview.Create(readme, markdown: true);
            ReadmeReader.Visibility = Visibility.Visible;
            ReadmeStatus.Text = "";
            ReadmeReader.Focus();
        }, ReadmeStatus);
    }

    private void ReadmeBackClick(object sender, RoutedEventArgs e) => CloseReadme();

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (ReadmeView.Visibility != Visibility.Visible)
            return;

        if (e.Key == Key.Escape)
        {
            CloseReadme();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control &&
                 ApplicationCommands.Find.CanExecute(null, ReadmeReader))
        {
            ApplicationCommands.Find.Execute(null, ReadmeReader);
            e.Handled = true;
        }
    }

    private void CloseReadme()
    {
        _operation?.Cancel();
        ReadmeView.Visibility = Visibility.Collapsed;
        ReadmeReader.Visibility = Visibility.Collapsed;
        ReadmeReader.Document = null;
        RepositoryBrowser.Visibility = Visibility.Visible;
        if (!ReadmeButton.Focus())
            CloseButton.Focus();
    }

    private void UpdateActions()
    {
        if (CloneButton is null)
            return;
        bool idle = _operation is null;
        if (CloneUrlButton is not null && RepositoryUrl is not null)
        {
            RepositoryUrl.IsEnabled = idle;
            CloneUrlButton.IsEnabled = idle && _workspaceRoot is not null && !string.IsNullOrWhiteSpace(RepositoryUrl.Text);
        }
        Search.IsEnabled = WritableOnly.IsEnabled = Owners.IsEnabled = idle && _account is not null;
        RefreshButton.IsEnabled = Repositories.IsEnabled = idle;
        bool hasSelection = idle && _account is not null && Repositories.CurrentItem is GitHubRepository;
        CloneButton.IsEnabled = hasSelection && (_workspaceRoot is not null || _remoteProject is not null);
        ReadmeButton.IsEnabled = OpenRepositoryButton.IsEnabled = hasSelection;
        CloseButton.Content = idle ? Loc.Text("L7B31A9FC4816") : Loc.Text("L72278748133A");
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        if (_operation is { } operation)
            operation.Cancel();
        else
            Close();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is { } operation)
        {
            e.Cancel = true;
            operation.Cancel();
        }
    }

    private void OpenDevicePageClick(object sender, RoutedEventArgs e) => OpenLink(GitHubCli.DeviceLogin.AbsoluteUri);

    private void OpenRepositoryClick(object sender, RoutedEventArgs e)
    {
        if (Repositories.CurrentItem is GitHubRepository repo)
            OpenLink($"https://github.com/{repo.FullName}");
    }

    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception ex) { Status.Text = ex.Message; }
    }
}
