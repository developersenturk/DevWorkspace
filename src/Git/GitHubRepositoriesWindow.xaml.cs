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
            Title = "GitHub’dan uzak depo seç";
            CloneButton.Content = "Bu depoyu seç";
            WritableOnly.IsChecked = true;
        }
        else if (workspaceRoot is null)
            CloneButton.ToolTip = "Depo eklemek için önce bir çalışma alanı açın.";
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private Task RefreshAsync() => RunAsync("GitHub depoları yükleniyor…", ReadRepositoriesAsync);

    private async Task ReadRepositoriesAsync(CancellationToken token)
    {
        string? previousOwner = (Owners.SelectedItem as OwnerChoice)?.Login;
        string? previousRepository = (Repositories.CurrentItem as GitHubRepository)?.FullName;
        _account = null;
        Account.Text = "GitHub hesabı";
        _repositories = [];
        Owners.ItemsSource = null;
        Repositories.ItemsSource = _repositories;
        EmptyResults.Visibility = Visibility.Collapsed;
        string account = await GitHubCli.ReadAccountAsync(token);
        if (string.IsNullOrWhiteSpace(account))
            throw new IOException("GitHub hesabı doğrulanamadı.");
        _repositories = await GitHubCli.ReadRepositoriesAsync(token);
        var groups = _repositories.GroupBy(repo => repo.Owner, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                bool ownAccount = string.Equals(group.Key, account, StringComparison.OrdinalIgnoreCase);
                string kind = ownAccount ? "Kişisel hesabım" : group.First().IsOrganization ? "Organizasyon" : "Kişisel hesap";
                return new OwnerChoice(group.Key, group.Key, kind, group.Count());
            })
            .OrderByDescending(owner => string.Equals(owner.Login, account, StringComparison.OrdinalIgnoreCase))
            .ThenBy(owner => owner.Label, StringComparer.OrdinalIgnoreCase);
        OwnerChoice[] owners = [new(null, "Tüm depolar", "Tüm sahipler", _repositories.Length), .. groups];
        Owners.ItemsSource = owners;
        Owners.SelectedItem = owners.FirstOrDefault(owner => string.Equals(owner.Login, previousOwner, StringComparison.OrdinalIgnoreCase))
            ?? owners[0];
        _account = account;
        Account.Text = $"@{account}   ·   {_repositories.Length} depo";
        ApplySearch(previousRepository);
    }

    private async void InstallClick(object sender, RoutedEventArgs e) =>
        await RunAsync("GitHub CLI kuruluyor…", async token =>
        {
            await GitHubCli.InstallAsync(token);
            await ReadRepositoriesAsync(token);
        });

    private async void LoginClick(object sender, RoutedEventArgs e) =>
        await RunAsync("GitHub giriş kodu alınıyor…", async token =>
        {
            var progress = new Progress<string>(code =>
            {
                if (_operation?.Token != token || token.IsCancellationRequested)
                    return;
                DeviceCode.Text = code;
                DevicePanel.Visibility = Visibility.Visible;
                Status.Text = "Tarayıcıda bu kodu girip GitHub girişini onaylayın.";
            });
            try { await GitHubCli.LoginAsync(progress, token); }
            finally
            {
                DeviceCode.Clear();
                DevicePanel.Visibility = Visibility.Collapsed;
            }
            Status.Text = "GitHub depoları yükleniyor…";
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

        var picker = new OpenFolderDialog
        {
            Title = $"{repository.FullName} için üst klasörü seçin",
            InitialDirectory = _workspaceRoot
        };
        if (picker.ShowDialog(this) != true)
            return;

        await RunAsync($"{repository.FullName} indiriliyor…", async token =>
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
            Account.Text = "GitHub hesabı";
            Status.Text = ex.Message;
            feedback.Text = ex.Message;
            LoginButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            feedback.Text = "İşlem iptal edildi.";
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
        ListHeading.Text = owner?.Label ?? "Tüm depolar";
        OwnerColumn.Visible = owner?.Login is null;
        Repositories.ItemsSource = visible;
        Repositories.CurrentItem = visible.FirstOrDefault(repo => repo.FullName.Equals(previousRepository, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault();
        EmptyResults.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = $"{visible.Length} / {owner?.Count ?? _repositories.Length} depo";
        if (_workspaceRoot is null)
            Status.Text += " · Depo eklemek için önce bir çalışma alanı açın.";
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
            DetailOwner.Text = $"{repo.Owner} · {(repo.IsOrganization ? "Organizasyon" : "Kişisel hesap")}";
            DetailDescription.Text = repo.DescriptionLabel + (string.IsNullOrWhiteSpace(repo.Description) ? ". README’den inceleyebilirsiniz." : "");
            DetailAccess.Text = $"{repo.VisibilityLabel} · {repo.AccessLabel}";
            DetailTechnology.Text = $"Dil · {repo.Language ?? "Belirtilmemiş"}\nVarsayılan dal · {repo.DefaultBranch ?? "—"}";
            DetailActivity.Text = repo.PushedAt is { Year: > 1 } pushed
                ? $"Son push · {pushed.ToLocalTime():dd.MM.yyyy}" : "Son push · —";
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
        await RunAsync("README yükleniyor…", async token =>
        {
            string readme = await GitHubCli.ReadRepositoryReadmeAsync(repo, token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(readme))
            {
                ReadmeStatus.Text = "Bu depo için görüntülenebilir bir README bulunamadı.";
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
        Search.IsEnabled = WritableOnly.IsEnabled = Owners.IsEnabled = idle && _account is not null;
        RefreshButton.IsEnabled = Repositories.IsEnabled = idle;
        bool hasSelection = idle && _account is not null && Repositories.CurrentItem is GitHubRepository;
        CloneButton.IsEnabled = hasSelection && (_workspaceRoot is not null || _remoteProject is not null);
        ReadmeButton.IsEnabled = OpenRepositoryButton.IsEnabled = hasSelection;
        CloseButton.Content = idle ? "Kapat" : "İptal";
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
