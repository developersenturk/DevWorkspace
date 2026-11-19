using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class GitHubPublishWindow : ThemedWindow
{
    private readonly GitWorkbench _git;
    private readonly GitStatus _expected;
    private string? _account;
    private bool _busy;
    private bool _workflowPermissionRequired;
    private CancellationTokenSource? _loginCancellation;

    internal Uri? RepositoryUrl { get; private set; }
    internal bool RepositoryCreated => RepositoryUrl is not null;
    internal bool Published { get; private set; }

    internal GitHubPublishWindow(GitWorkbench git, GitStatus expected, string suggestedName)
    {
        _git = git;
        _expected = expected;
        InitializeComponent();
        BranchDescription.Text = Loc.Format("LBAF0DAB21C6F", expected.Branch);
        RepositoryName.Text = Regex.Replace(suggestedName[..Math.Min(suggestedName.Length, 100)], "[^A-Za-z0-9_.-]", "-");
    }

    private void NameChanged(object sender, RoutedEventArgs e) => UpdateActions();
    private void OwnerChanged(object sender, RoutedEventArgs e) => UpdateActions();
    private async void WindowLoaded(object sender, RoutedEventArgs e) =>
        await RunAsync(Loc.Text("L0AE0978610F5"), ReadAccountAsync);

    private async void RetryClick(object sender, RoutedEventArgs e) =>
        await RunAsync(Loc.Text("L0AE0978610F5"), ReadAccountAsync);

    private async void InstallClick(object sender, RoutedEventArgs e) =>
        await RunAsync("GitHub CLI kuruluyor…", async () =>
        {
            await GitHubCli.InstallAsync(CancellationToken.None);
            await ReadAccountAsync();
        });

    private async Task ReadAccountAsync()
    {
        var previousOwner = RepositoryOwner.SelectedItem as GitHubRepositoryOwner;
        _account = null;
        RepositoryOwner.ItemsSource = null;
        string account = await GitHubCli.ReadAccountAsync(CancellationToken.None);
        if (!Regex.IsMatch(account, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}\z"))
            throw new IOException(Loc.Text("LB9355E9BC2E7"));

        string[] organizations;
        try
        {
            organizations = await GitHubCli.ReadOrganizationsAsync(CancellationToken.None);
            Status.Text = organizations.Length == 0 ? Loc.Format("LD1010F993F3E", account)
                : Loc.Format("L9D9E8EBB43BD", account);
        }
        catch (IOException ex) when (ex is not (GitHubCliMissingException or GitHubLoginRequiredException))
        {
            organizations = [];
            Status.Text = Loc.Format("L620C51047598", ex.Message);
            RetryButton.Visibility = Visibility.Visible;
        }

        _account = account;
        GitHubRepositoryOwner[] owners = [new(account, false), .. organizations.Select(org => new GitHubRepositoryOwner(org, true))];
        RepositoryOwner.ItemsSource = owners;
        RepositoryOwner.SelectedItem = owners.FirstOrDefault(owner => owner == previousOwner) ?? owners[0];
    }

    private async void LoginClick(object sender, RoutedEventArgs e) =>
        await RunAsync(Loc.Text("L332073CC27FA"), () => SignInAsync(false));

    private async void WorkflowPermissionClick(object sender, RoutedEventArgs e) =>
        await RunAsync(Loc.Text("L3CF24D68A8B7"), () => SignInAsync(true));

    private async Task SignInAsync(bool authorizeWorkflows)
    {
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        UpdateActions();
        var progress = new Progress<string>(code =>
        {
            if (_loginCancellation != cancellation || cancellation.IsCancellationRequested)
                return;
            DeviceCode.Text = code;
            DevicePanel.Visibility = Visibility.Visible;
            Status.Text = Loc.Text("LA5FC7022AE90");
        });
        try
        {
            if (authorizeWorkflows)
            {
                if (!string.Equals(_account, await GitHubCli.ReadAccountAsync(cancellation.Token), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Loc.Text("L398AA24E6615"));
                await GitHubCli.AuthorizeWorkflowsAsync(progress, cancellation.Token);
                await GitHubCli.VerifyWorkflowPermissionAsync(cancellation.Token);
                _workflowPermissionRequired = false;
                Status.Text = Loc.Text("LD15E88EA1950");
            }
            else
            {
                await GitHubCli.LoginAsync(progress, cancellation.Token);
                await ReadAccountAsync();
                _workflowPermissionRequired = false;
            }
        }
        finally
        {
            _loginCancellation = null;
            DeviceCode.Clear();
            DevicePanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void PublishClick(object sender, RoutedEventArgs e)
    {
        if (!Publish.IsEnabled || _account is not string account ||
            RepositoryOwner.SelectedItem is not GitHubRepositoryOwner owner)
            return;

        string name = RepositoryName.Text.Trim();
        bool isPrivate = PrivateRepository.IsChecked == true;
        var url = RepositoryUrl ?? owner.RepositoryUrl(name);
        if (!isPrivate && !RepositoryCreated &&
            MessageBox.Show(this, Loc.Format("L08C4742FA57D", owner.Login, name, _expected.Branch),
                Loc.Text("L0C57CDE78FE5"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunAsync(RepositoryCreated ? Loc.Text("LD310635F609B") : Loc.Text("LC7122CA9D4EF"), async () =>
        {
            if (RepositoryCreated && !string.Equals(account, await GitHubCli.ReadAccountAsync(CancellationToken.None), StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.Text("L398AA24E6615"));
            await _git.VerifyGitHubPublishPermissionAsync(_expected, CancellationToken.None);
            if (!RepositoryCreated)
            {
                await _git.VerifyNewPublishAsync(_expected, CancellationToken.None);
                await GitHubCli.CreateRepositoryAsync(account, owner, name, isPrivate, CancellationToken.None);
                RepositoryUrl = url;
            }
            Status.Text = Loc.Format("L3CD9FA718B19", _expected.Branch);
            await _git.PublishToGitHubAsync(_expected, url, CancellationToken.None);
            Published = true;
            Status.Text = Loc.Format("LF01624E2345E", url);
        });
    }

    private async Task RunAsync(string message, Func<Task> operation)
    {
        if (_busy)
            return;

        _busy = true;
        Status.Text = message;
        InstallButton.Visibility = LoginButton.Visibility = RetryButton.Visibility = Visibility.Collapsed;
        UpdateActions();
        try { await operation(); }
        catch (GitHubCliMissingException ex)
        {
            Status.Text = ex.Message;
            InstallButton.Visibility = Visibility.Visible;
        }
        catch (GitHubLoginRequiredException ex)
        {
            _account = null;
            Status.Text = ex.Message;
            LoginButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            Status.Text = Loc.Text("L69B68B59862C");
            if (!_workflowPermissionRequired)
                LoginButton.Visibility = Visibility.Visible;
        }
        catch (GitHubWorkflowPermissionException ex)
        {
            _workflowPermissionRequired = true;
            Status.Text = RepositoryCreated ? Loc.Format("LA7559CC89F66", ex.Message) : ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Status.Text = RepositoryCreated ? Loc.Format("LA7559CC89F66", ex.Message) : ex.Message;
            if (_account is null)
                RetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private void UpdateActions()
    {
        if (Publish is null || CloseButton is null)
            return;

        string name = RepositoryName.Text.Trim();
        var owner = RepositoryOwner.SelectedItem as GitHubRepositoryOwner;
        RepositoryOwner.IsEnabled = !_busy && !RepositoryCreated && _account is not null;
        RepositoryName.IsEnabled = PrivateRepository.IsEnabled = !_busy && !RepositoryCreated;
        RepositoryTarget.Text = RepositoryUrl?.AbsoluteUri ??
            (owner is null || name.Length == 0 ? "" : $"github.com/{owner.Login}/{name}");
        WorkflowPermissionButton.Visibility = _workflowPermissionRequired && _account is not null
            ? Visibility.Visible : Visibility.Collapsed;
        WorkflowPermissionButton.IsEnabled = !_busy;
        Publish.IsEnabled = !_busy && !_workflowPermissionRequired && _account is not null && !Published && owner is not null &&
            (RepositoryCreated || Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]{1,100}\z") &&
                name is not ("." or "..") && !name.EndsWith(".git", StringComparison.OrdinalIgnoreCase));
        Publish.Content = RepositoryCreated ? Loc.Text("LA67EA82419AF") : Loc.Text("L88C147DB2E07");
        Publish.Visibility = Published ? Visibility.Collapsed : Visibility.Visible;
        OpenRepository.Visibility = RepositoryCreated ? Visibility.Visible : Visibility.Collapsed;
        OpenRepository.IsEnabled = !_busy;
        CloseButton.Content = _loginCancellation is null ? Loc.Text("L7B31A9FC4816") : Loc.Text("LCFE11711548C");
        CloseButton.IsEnabled = !_busy || _loginCancellation is not null;
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            _loginCancellation?.Cancel();
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
            _loginCancellation?.Cancel();
        else
            Close();
    }

    private void OpenDevicePageClick(object sender, RoutedEventArgs e) => OpenLink(GitHubCli.DeviceLogin);
    private void OpenRepositoryClick(object sender, RoutedEventArgs e) => OpenLink(RepositoryUrl!);

    private void OpenLink(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception ex) { Status.Text = ex.Message; }
    }
}
