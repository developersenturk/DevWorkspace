using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevExpress.Xpf.Core;
using Microsoft.Win32;

namespace DevWorkspace;

public partial class ReleaseCenterWindow : ThemedWindow
{
    private readonly ReleaseStore _store;
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly string? _initialProject;
    private CancellationTokenSource? _operation;
    private ReleasePackage? _package;
    private RemoteRelease[] _releases = [];
    private string? _statsRepository;
    private Action<string>? _activeLog;
    internal bool IsWorking => _operation is not null;

    internal ReleaseCenterWindow(string? project = null) : this(project, new ReleaseStore()) { }

    internal ReleaseCenterWindow(string? project, ReleaseStore store)
    {
        _store = store;
        InitializeComponent();
        _initialProject = project;
        ApplyProfile(new());
        try { RefreshLocal(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status.Text = e.Message; }
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
        Loaded += WindowLoaded;
        Closed += (_, _) => _timer.Stop();
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WindowLoaded;
        if (_initialProject is not null) await SelectProjectAsync(_initialProject);
    }
    internal async Task SelectProjectAsync(string project)
    {
        if (IsWorking) { Status.Text = ReleaseText.T("StopFirst"); return; }
        if (string.Equals(ProjectFile.Text, Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase)) return;
        await RunAsync("Project", async (service, token) =>
        {
            if (ProjectFile.Text.Length > 0) _store.SaveProfile(Profile());
            // Drop the old target before awaiting I/O: a failed switch must never leave another project's publish state active.
            ClearContext();
            var detected = await service.DetectAsync(project, token);
            var saved = _store.LoadProjectProfile(detected.ProjectFile);
            string version = saved?.Version ?? detected.Version;
            if (Version.TryParse(detected.Version, out var current) && (!Version.TryParse(version, out var previous) || current > previous))
                version = detected.Version;
            ApplyProfile((saved ?? detected) with { ProjectFile = detected.ProjectFile, Repository = detected.Repository, Version = version });
            RefreshLocal();
        });
    }
    private void ClearContext()
    {
        _package = null;
        ApplyProfile(new());
        UploadButton.IsEnabled = PublishButton.IsEnabled = false;
        PackageSummary.Text = ReleaseText.T("NoPackage");
        History.ItemsSource = null;
        _releases = [];
        _statsRepository = null;
        ReleasesGrid.ItemsSource = AssetsGrid.ItemsSource = null;
        DownloadTotal.Text = "—";
        Breakdown.Text = RepositoryStats.Text = StatsHint.Text = "";
        ExportButton.IsEnabled = false;
        CommitMessage.Clear();
        Output.Clear();
        lock (_pending) _pending.Clear();
    }
    private ReleaseProfile Profile() => new(ProjectFile.Text.Trim(), Repository.Text.Trim(), AppId.Text.Trim(),
        VersionNumber.Text.Trim(), Notes.Text, TestProjects.Text, RequireChecks.IsChecked == true);
    private void ApplyProfile(ReleaseProfile profile)
    {
        ProjectFile.Text = profile.ProjectFile;
        Repository.Text = profile.Repository;
        AppId.Text = profile.AppId;
        VersionNumber.Text = profile.Version;
        Notes.Text = profile.Notes;
        TestProjects.Text = profile.TestProjects;
        RequireChecks.IsChecked = profile.RequireChecks;
        string name = Path.GetFileNameWithoutExtension(profile.ProjectFile);
        Title = name.Length == 0 ? ReleaseText.T("Title") : ReleaseText.T("Title") + " · " + name;
        ContextCaption.Text = name.Length == 0 ? ReleaseText.T("SelectContext") : name + " · " + profile.Repository;
    }
    private void SaveProfile()
    {
        _store.SaveProfile(Profile());
        RefreshLocal();
    }
    private void RefreshLocal()
    {
        History.ItemsSource = _store.Packages().Where(p => string.Equals(p.ProjectFile, ProjectFile.Text, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Repository, Repository.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (_package is not null) History.SelectedItem = ((ReleasePackage[])History.ItemsSource).FirstOrDefault(p => p.Directory == _package.Directory);
    }
    private static string ResolveTool(string tool) => tool switch
    {
        "dotnet" => DevelopmentTools.Require(DevelopmentTool.DotNet),
        "git" => DevelopmentTools.Require(DevelopmentTool.Git),
        "gh" => DevelopmentTools.Require(DevelopmentTool.GitHub),
        _ => tool
    };
    private async Task RunAsync(string name, Func<ReleaseService, CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        if (name is "Build" or "Upload" or "Publish" or "Check" or "Probe" or "Push" or "Commit") LogExpander.IsExpanded = true;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        SetWorking(true);
        var entry = OperationCenter.Start(ReleaseText.T("Title") + " · " + ReleaseText.T(name), Repository.Text);
        entry.Cancel = cancellation.Cancel;
        string result = ReleaseText.T("Success");
        string? error = null;
        try
        {
            Directory.CreateDirectory(_store.DirectoryPath);
            // A second application instance must not modify the same drafts or package tool cache concurrently.
            await using var lease = new FileStream(Path.Combine(_store.DirectoryPath, "operation.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            string logs = Path.Combine(_store.DirectoryPath, "logs");
            Directory.CreateDirectory(logs);
            await using var writer = new StreamWriter(Path.Combine(logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log"));
            object gate = new();
            void Log(string line)
            {
                lock (gate) { writer.WriteLine(line); writer.Flush(); }
                lock (_pending)
                {
                    _pending.AppendLine(line);
                    if (_pending.Length > 160000) _pending.Remove(0, _pending.Length - 160000);
                }
                entry.Append(line + Environment.NewLine);
            }
            Log($"{DateTimeOffset.Now:O} · {ReleaseText.T(name)}");
            Status.Text = ReleaseText.T(name) + "…";
            var service = new ReleaseService(new ReleaseProcess(ResolveTool), _store, Log);
            _activeLog = Log;
            try { await action(service, cancellation.Token); }
            catch (Exception ex) { Log(ex.Message); throw; }
            Log(ReleaseText.T("Success"));
        }
        catch (OperationCanceledException) { result = ReleaseText.T("Canceled"); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            result = ReleaseText.T("OtherOperation");
            error = result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or JsonException or System.Xml.XmlException or Win32Exception)
        {
            result = ex is ToolMissingException ? ex.Message + " " + ReleaseText.T("ToolsHint") : ex.Message;
            error = result;
        }
        finally
        {
            _activeLog = null;
            _operation = null;
            Flush();
            Status.Text = result;
            if (error is not null) LogExpander.IsExpanded = true;
            entry.Finish(error is null ? result : Loc.Text("LF0873175A881"), error);
            SetWorking(false);
        }
    }
    private void SetWorking(bool working)
    {
        FormPanel.IsEnabled = !working;
        ConnectionButton.IsEnabled = !working;
        ActionPanel.IsEnabled = !working;
        StatsActions.IsEnabled = HistoryActions.IsEnabled = History.IsEnabled = !working;
        StopButton.IsEnabled = working;
        Progress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Flush()
    {
        string text;
        lock (_pending) { text = _pending.ToString(); _pending.Clear(); }
        if (text.Length == 0) return;
        Output.AppendText(text);
        if (Output.Text.Length > 160000) Output.Text = Output.Text[^160000..];
        Output.ScrollToEnd();
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is not null) { e.Cancel = true; Status.Text = ReleaseText.T("StopFirst"); }
    }
    private void StopClick(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Tabs) && Tabs.SelectedIndex == 1 && _operation is null && LogExpander is not null)
            LogExpander.IsExpanded = false;
    }
    private async void SaveProfileClick(object sender, RoutedEventArgs e) =>
        await RunAsync("SaveProfile", (_, _) => { SaveProfile(); return Task.CompletedTask; });
    private async void CheckClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Check", (service, token) => service.CheckAsync(Profile(), token));
    private async void NextVersionClick(object sender, RoutedEventArgs e) =>
        await RunAsync("NextVersion", async (service, token) =>
        {
            var versions = (await service.ReleasesAsync(Profile().Repository, token)).Where(r => !r.Draft && !r.Prerelease)
                .Select(r => System.Version.TryParse(r.Tag.TrimStart('v'), out var v) ? v : null).OfType<Version>();
            if (System.Version.TryParse(VersionNumber.Text, out var current)) versions = versions.Append(current);
            var version = versions.OrderDescending().FirstOrDefault() ?? new Version(1, 0, 0);
            VersionNumber.Text = $"{version.Major}.{version.Minor}.{checked(Math.Max(0, version.Build) + 1)}";
        });
    private async void GenerateNotesClick(object sender, RoutedEventArgs e) =>
        await RunAsync("GenerateNotes", async (service, token) =>
        {
            var previous = (await service.ReleasesAsync(Profile().Repository, token)).Where(r => !r.Draft && !r.Prerelease)
                .OrderByDescending(r => r.Published).FirstOrDefault();
            Notes.Text = await service.NotesAsync(Profile(), previous?.Tag, token);
        });
    private async void CommitClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Commit", async (service, token) =>
        {
            string root = await service.RootAsync(ProjectFile.Text, token);
            string status = await service.StatusAsync(root, token);
            if (string.IsNullOrWhiteSpace(status)) { Status.Text = ReleaseText.T("Clean"); return; }
            string message = CommitMessage.Text.Trim();
            if (message.Length == 0) throw new IOException(ReleaseText.T("CommitMessage"));
            if (MessageBox.Show(this, ReleaseText.F("ConfirmCommit", status, message), ReleaseText.T("Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                await service.CommitAsync(root, message, token);
        });
    private async void PushClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Push", (service, token) => service.PushAsync(Profile(), token));
    private async void BuildClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Build", async (service, token) =>
        {
            SaveProfile();
            SetPackage(await service.BuildAsync(Profile(), token));
            RefreshLocal();
        });
    private void SetPackage(ReleasePackage package)
    {
        _package = package;
        PackageSummary.Text = package.Label;
        UploadButton.IsEnabled = PublishButton.IsEnabled = true;
    }
    private ReleasePackage SelectedPackage(bool matchForm = true)
    {
        var package = matchForm ? _package : History.SelectedItem as ReleasePackage ?? _package;
        if (package is null) throw new IOException(ReleaseText.T("SelectPackage"));
        var p = Profile();
        if (matchForm && (p.ProjectFile != package.ProjectFile || p.AppId != package.AppId ||
            p.Version != package.Version || !ReleaseRules.Repository(p.Repository).Equals(package.Repository, StringComparison.OrdinalIgnoreCase)))
            throw new IOException(ReleaseText.T("ProfileMismatch"));
        return package;
    }
    private async void UploadClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Upload", (service, token) => service.UploadDraftAsync(SelectedPackage(), Notes.Text, token));
    private async void PublishClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Publish", async (service, token) =>
        {
            var package = SelectedPackage();
            if (MessageBox.Show(this, ReleaseText.F("ConfirmPublish", ReleaseText.T("Title"), package.Repository,
                package.Version, package.Commit), ReleaseText.T("Title"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await service.PublishAsync(package, RequireChecks.IsChecked == true, token, Notes.Text);
        });
    private async void UsePackageClick(object sender, RoutedEventArgs e) =>
        await RunAsync("UsePackage", (_, _) =>
        {
            var package = SelectedPackage(false);
            string notesPath = Path.Combine(package.Directory, "release-notes.md");
            string profilePath = Path.Combine(package.Directory, "profile.json");
            var saved = File.Exists(profilePath) ? JsonSerializer.Deserialize<ReleaseProfile>(File.ReadAllText(profilePath), ReleaseStore.Json) : null;
            ApplyProfile((saved ?? new()) with { ProjectFile = package.ProjectFile, Repository = package.Repository, AppId = package.AppId,
                Version = package.Version, Notes = File.Exists(notesPath) ? File.ReadAllText(notesPath) : "" });
            SetPackage(package);
            Tabs.SelectedIndex = 0;
            return Task.CompletedTask;
        });
    private async void VerifyLocalClick(object sender, RoutedEventArgs e) =>
        await RunAsync("VerifyLocal", (_, token) => ReleaseStore.VerifyAsync(SelectedPackage(false), token));
    private async void VerifyClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Verify", async (service, token) => { await service.VerifyRemoteAsync(SelectedPackage(false), token); });
    private async void ChecksClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Checks", async (service, token) =>
        {
            var package = SelectedPackage(false);
            await service.ChecksAsync(package.Repository, package.Commit, token);
        });
    private async void RefreshClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Refresh", async (service, token) =>
        {
            // Clear first so failed refreshes never present data from another repository as current.
            _releases = [];
            _statsRepository = null;
            ReleasesGrid.ItemsSource = AssetsGrid.ItemsSource = null;
            DownloadTotal.Text = "—";
            Breakdown.Text = RepositoryStats.Text = StatsHint.Text = "";
            ExportButton.IsEnabled = false;
            string repo = ReleaseRules.Repository(Repository.Text);
            var releases = await service.ReleasesAsync(repo, token);
            string[] stats = (await service.RepositoryStatsAsync(repo, token)).Trim().Split('\t');
            _releases = releases;
            _statsRepository = repo;
            ReleasesGrid.ItemsSource = releases;
            ReleasesGrid.CurrentItem = releases.FirstOrDefault();
            var assets = releases.Where(r => !r.Draft).SelectMany(r => r.Assets).ToArray();
            DownloadTotal.Text = assets.Where(a => ReleaseRules.IsDownload(a.Name)).Sum(a => a.Downloads).ToString("N0");
            Breakdown.Text = string.Join("   ·   ", new[] { "Installer", "Portable", "UpdatePackage" }.Select(kind =>
                ReleaseText.T(kind) + ": " + assets.Where(a => a.Kind == ReleaseText.T(kind)).Sum(a => a.Downloads).ToString("N0")));
            if (stats.Length == 3) RepositoryStats.Text = repo + "   ·   " + string.Join("   ·   ", new[] { "Stars", "Forks", "Issues" }
                .Select((name, i) => ReleaseText.T(name) + ": " + stats[i]));
            StatsHint.Text = ReleaseText.F("StatsHint", DateTime.Now.ToString("g"));
            ExportButton.IsEnabled = true;
        });
    private async void ProbeClick(object sender, RoutedEventArgs e) =>
        await RunAsync("Probe", async (_, token) =>
        {
            await ReleaseDownloadVerifier.VerifyAsync(Repository.Text, AppId.Text, _store,
                line => _activeLog?.Invoke(line), token);
        });
    private void ReleaseChanged(object sender, RoutedEventArgs e) => AssetsGrid.ItemsSource = (ReleasesGrid.CurrentItem as RemoteRelease)?.Assets;
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (_statsRepository is null) return;
        var picker = new SaveFileDialog { Title = ReleaseText.T("Export"), Filter = "CSV (*.csv)|*.csv", FileName = "release-downloads.csv" };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync("Export", async (_, token) =>
        {
            var rows = new List<string> { "Repository,Tag,State,Asset,Kind,Downloads,Bytes" };
            rows.AddRange(_releases.SelectMany(r => r.Assets.Select(a => string.Join(',', new[] { _statsRepository!, r.Tag,
                r.State, a.Name, a.Kind, a.Downloads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                a.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Select(ReleaseRules.Csv)))));
            await File.WriteAllLinesAsync(picker.FileName, rows, new UTF8Encoding(true), token);
        });
    }
    private async void OpenFolderClick(object sender, RoutedEventArgs e) =>
        await RunAsync("OpenFolder", (_, _) => { OpenPath(SelectedPackage(false).Directory); return Task.CompletedTask; });
    private void OpenLogsClick(object sender, RoutedEventArgs e)
    {
        try { string folder = Path.Combine(_store.DirectoryPath, "logs"); Directory.CreateDirectory(folder); OpenPath(folder); }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException) { Status.Text = ex.Message; }
    }
    private void ConnectionClick(object sender, RoutedEventArgs e) => new GitHubRepositoriesWindow(null) { Owner = this }.ShowDialog();
    private void OpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (ReleasesGrid.CurrentItem is RemoteRelease release && Uri.TryCreate(release.Url, UriKind.Absolute, out var url) &&
            url.Scheme == "https" && url.Host == "github.com" && url.UserInfo.Length == 0)
            try { OpenPath(url.AbsoluteUri); } catch (Win32Exception ex) { Status.Text = ex.Message; }
    }
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
