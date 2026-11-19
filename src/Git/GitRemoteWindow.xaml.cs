using System.Windows;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class GitRemoteWindow : ThemedWindow
{
    private readonly string[] _remotes;
    private readonly string[] _repositories;
    private readonly HashSet<string> _connectedUrls;
    private readonly string _project;
    private CancellationTokenSource? _discovery;
    private GitHubRepository? _github;

    internal GitRemoteWindow(string[] remotes, string[] repositories, IEnumerable<GitRemote> connected, string project, bool canPublish)
    {
        _remotes = remotes;
        _repositories = repositories;
        _connectedUrls = connected.Select(remote => remote.Url).OfType<string>().ToHashSet(StringComparer.Ordinal);
        _project = project;
        InitializeComponent();
        ProjectHeading.Text = Loc.Format("LC8165CB52914", project);
        CreateGitHubButton.IsEnabled = canPublish;
        CreateGitHubButton.ToolTip = canPublish ? Loc.Text("LBF67761CEE73")
            : Loc.Text("L721B53EEA622");
        NameInput.Text = SuggestedName("remote");
        if (remotes.Length == 0)
            CancelButton.Content = Loc.Text("LCF5183260E94");
    }

    internal string RemoteName => NameInput.Text.Trim();
    internal string RemoteUrl => UrlInput.Text.Trim();
    internal bool CreateGitHub { get; private set; }
    internal GitHubRepository? SelectedGitHub => _github is { } repo && RemoteUrl == repo.HttpsCloneUrl ? repo : null;

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        using var cancellation = new CancellationTokenSource();
        _discovery = cancellation;
        DiscoveryStatus.Text = Loc.Text("LCD6240133153");
        try
        {
            var targets = await GitWorkbench.DiscoverRemotesAsync(_repositories, cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;
            KnownTargets.ItemsSource = targets;
            KnownTargets.IsEnabled = targets.Length > 0;
            DiscoveryStatus.Text = targets.Length == 0 ? Loc.Text("LAF7331EC9E11") : "";
            DiscoveryStatus.Visibility = targets.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { _discovery = null; }
    }

    private void WindowClosed(object? sender, EventArgs e) => _discovery?.Cancel();

    private void KnownTargetChanged(object sender, RoutedEventArgs e)
    {
        if (KnownTargets.SelectedItem is GitRemoteSuggestion suggestion)
        {
            UrlInput.Text = suggestion.Prefix;
            UrlInput.Focus();
            UrlInput.CaretIndex = UrlInput.Text.Length;
        }
    }

    private void ChooseGitHubClick(object sender, RoutedEventArgs e)
    {
        var picker = new GitHubRepositoriesWindow(null, _project) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedRemote is { } repo)
        {
            _github = repo;
            if (RemoteName == SuggestedName("remote"))
                NameInput.Text = SuggestedName("github");
            UrlInput.Text = repo.HttpsCloneUrl;
        }
    }

    private void CreateGitHubClick(object sender, RoutedEventArgs e)
    {
        CreateGitHub = true;
        DialogResult = true;
    }

    private string SuggestedName(string prefix)
    {
        string name = _remotes.Length == 0 ? "origin" : prefix;
        for (int suffix = 2; _remotes.Contains(name, StringComparer.OrdinalIgnoreCase); suffix++)
            name = $"{prefix}-{suffix}";
        return name;
    }

    private void InputChanged(object sender, RoutedEventArgs e)
    {
        if (AddButton is null)
            return;

        string name = RemoteName;
        string url = RemoteUrl;
        bool validUrl = GitWorkbench.IsSupportedRemoteUrl(url);
        bool needsPath = KnownTargets.SelectedItem is GitRemoteSuggestion suggestion && url == suggestion.Prefix;
        InputStatus.Text = name.Length == 0 ? "" :
            !GitWorkbench.IsSupportedRemoteName(name) ? Loc.Text("LF843C1FC6B52") :
            _remotes.Contains(name, StringComparer.OrdinalIgnoreCase) ? Loc.Text("LBAB26178BA59") :
            _connectedUrls.Contains(url) ? Loc.Text("L024383573C86") :
            url.Length > 0 && !validUrl && !needsPath ? Loc.Text("L66FA8D72D57E") : "";
        AddButton.IsEnabled = name.Length > 0 && validUrl && !needsPath && InputStatus.Text.Length == 0;
    }

    private void AddClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
