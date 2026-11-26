using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DevExpress.Xpf.Editors;

namespace DevWorkspace;

public partial class GitWorkbenchView : UserControl
{
    private readonly MainWindow _owner;
    internal ProjectEntry Project { get; }
    private readonly string _repository;
    private readonly GitWorkbench _git;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _preview;
    private GitWorkbenchState? _state;
    private bool _busy;
    private bool _updatingLists;

    internal bool IsBusy => _busy;

    private (GitFileChange File, bool Staged)[] DiffFiles =>
        (WorkingFiles.ItemsSource as GitFileChange[] ?? []).Select(file => (file, false))
        .Concat((StagedFiles.ItemsSource as GitFileChange[] ?? []).Select(file => (file, true))).ToArray();

    internal (int Index, int Count) DiffPosition
    {
        get
        {
            var files = DiffFiles;
            bool staged = StagedFiles.SelectedItem is not null;
            return (Array.FindIndex(files, item => item.File.Path == SelectedFile?.Path && item.Staged == staged), files.Length);
        }
    }

    internal void NavigateDiff(int direction)
    {
        if (_busy || _closing.IsCancellationRequested)
            return;
        var files = DiffFiles;
        int current = DiffPosition.Index;
        int next = current < 0 ? (direction > 0 ? 0 : files.Length - 1) : current + direction;
        if (next < 0 || next >= files.Length)
            return;
        var target = files[next];
        _updatingLists = true;
        WorkingFiles.UnSelectAll();
        StagedFiles.UnSelectAll();
        var list = target.Staged ? StagedFiles : WorkingFiles;
        (target.Staged ? StagedSection : WorkingSection).IsChecked = true;
        list.SelectedItem = target.File;
        _updatingLists = false;
        UpdateFileListLayout((WorkingFiles.ItemsSource as GitFileChange[])?.Length ?? 0,
            (StagedFiles.ItemsSource as GitFileChange[])?.Length ?? 0);
        UpdateActions();
        _owner.ShowGitDiff();
        _ = LoadDiffAsync();
    }

    internal GitWorkbenchView(MainWindow owner, ProjectEntry project)
    {
        _owner = owner;
        Project = project;
        _repository = project.Repository ?? throw new ArgumentException(Loc.Text("L4B1E1C5AFA51"));
        _git = new(_repository);
        InitializeComponent();
        RepositoryTitle.Text = project.Name;
        RepositoryTitle.ToolTip = _repository;
    }

    internal void Release()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    internal Task RefreshAsync() => RunOperationAsync(Loc.Text("L540A56493D07"));

    internal void OpenFirstChange()
    {
        if (SelectedFile is not null)
            _owner.ShowGitDiff();
        else if (WorkingFiles.ItemsSource is IEnumerable<GitFileChange> working && working.FirstOrDefault() is { } changed)
            WorkingFiles.SelectedItem = changed;
        else if (StagedFiles.ItemsSource is IEnumerable<GitFileChange> staged && staged.FirstOrDefault() is { } prepared)
            StagedFiles.SelectedItem = prepared;
    }

    internal GitViewSession CaptureSession(bool diffOpen) => new(
        Project.RelativePath, SelectedFile?.Path, StagedFiles.SelectedItem is not null, diffOpen,
        (FetchRemote.SelectedItem as GitRemote)?.Name, CommitMessage.Text);

    internal async Task RestoreSessionAsync(GitViewSession saved)
    {
        CommitMessage.EditValue = saved.Draft;
        await RefreshAsync();
        if (_closing.IsCancellationRequested)
            return;

        if (saved.FetchRemote is { } name && FetchRemote.ItemsSource is GitRemote[] remotes)
            FetchRemote.SelectedItem = remotes.FirstOrDefault(remote => remote.Name == name) ?? FetchRemote.SelectedItem;
        var list = saved.Staged ? StagedFiles : WorkingFiles;
        if (saved.File is { } path && list.ItemsSource is GitFileChange[] files)
        {
            _updatingLists = true;
            list.SelectedItem = files.FirstOrDefault(file => file.Path == path);
            _updatingLists = false;
            UpdateActions();
        }
        await LoadDiffAsync();
    }

    private async Task RunOperationAsync(string progress, Func<CancellationToken, Task>? action = null, string success = "")
    {
        if (_busy)
            return;

        _busy = true;
        StatusMessage.Text = progress;
        UpdateActions();
        string result = "";
        var operation = action is null ? null : OperationCenter.Start(progress, Project.Name);
        try
        {
            if (action is not null)
            {
                try
                {
                    await action(_closing.Token);
                    result = success;
                    operation?.Finish(Loc.Text("L7C6F3F5F37BC"), success);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    result = ex.Message;
                    operation?.Finish(Loc.Text("LF0873175A881"), result);
                }
            }

            GitWorkbenchState state = await _git.ReadAsync(_closing.Token);
            _owner.GitStatusObserved(_repository, state.Status);
            GitRemote[] remotes = await Task.WhenAll(state.Remotes.Select(remote => _git.ReadRemoteAsync(remote, _closing.Token)));
            await LoadStashListAsync();
            LoadState(state, remotes);
            StatusMessage.Text = string.IsNullOrEmpty(result)
                ? _state!.InProgress ?? _state.Status.Detail
                : result;
            if (string.IsNullOrWhiteSpace(StatusMessage.Text))
                StatusMessage.Text = Loc.Text("L53760CD15302");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            if (operation is { IsRunning: true })
                operation.Finish(Loc.Text("L7C925A8624F1"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _state = null;
            StagedFiles.ItemsSource = WorkingFiles.ItemsSource = null;
            StashList.ItemsSource = null;
            StashTitle.Text = Loc.Text("L7BF92BD00BEB");
            StashEmpty.Text = Loc.Text("L8D6D728E60FC");
            StashEmpty.ToolTip = ex.Message;
            BranchName.Text = Loc.Text("LC4AB169212B5");
            BranchStatus.Text = "";
            StagedTitle.Text = Loc.Text("L6305F6C5B5B8");
            WorkingTitle.Text = Loc.Text("L015C1B0968C6");
            UpdateFileListLayout(0, 0);
            PushSelectedItem.Items.Clear();
            FetchRemote.ItemsSource = null;
            UpdateFetchTooltip();
            StatusMessage.Text = string.IsNullOrEmpty(result) ? ex.Message : result + Environment.NewLine + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateActions();
            if (action is not null && !_closing.IsCancellationRequested)
                _owner.GitRepositoryChanged();
        }
    }

    private async Task LoadStashListAsync()
    {
        var selected = StashList.SelectedItem as GitStash;
        try
        {
            GitStash[] stashes = await _git.ReadStashesAsync(_closing.Token);
            StashList.ItemsSource = stashes;
            StashList.SelectedItem = stashes.FirstOrDefault(stash => stash.CommitId == selected?.CommitId);
            StashTitle.Text = Loc.Format("L26BB92FC4543", stashes.Length);
            StashEmpty.Text = Loc.Text("L781AF62B5E01");
            StashEmpty.ToolTip = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            StashList.ItemsSource = null;
            StashTitle.Text = Loc.Text("L7BF92BD00BEB");
            StashEmpty.Text = Loc.Text("L8D6D728E60FC");
            StashEmpty.ToolTip = ex.Message;
        }
    }

    private void LoadState(GitWorkbenchState state, GitRemote[] remotes)
    {
        var selected = SelectedFile;
        bool wasStaged = StagedFiles.SelectedItem is not null;
        string? previousFetchRemote = (FetchRemote.SelectedItem as GitRemote)?.Name;
        _state = state;
        GitFileChange[] staged = (state.Status.Files ?? []).Where(file => file.IsStaged).ToArray();
        GitFileChange[] working = (state.Status.Files ?? []).Where(file => file.HasWorkingChange).ToArray();
        _updatingLists = true;
        StagedFiles.ItemsSource = staged;
        WorkingFiles.ItemsSource = working;
        if (selected is not null)
        {
            GitFileChange? stagedMatch = staged.FirstOrDefault(file => file.Path == selected.Path);
            GitFileChange? workingMatch = working.FirstOrDefault(file => file.Path == selected.Path);
            if (wasStaged && stagedMatch is not null || workingMatch is null)
                StagedFiles.SelectedItem = stagedMatch;
            else
                WorkingFiles.SelectedItem = workingMatch;
        }
        _updatingLists = false;
        StagedTitle.Text = Loc.Format("LE0B41852CB37", staged.Length);
        WorkingTitle.Text = Loc.Format("LF515FF761779", working.Length);
        UpdateFileListLayout(working.Length, staged.Length);
        BranchName.Text = state.Status.Branch;
        BranchStatus.Text = state.Upstream is { } target
            ? $"{state.Status.Tracking}   ·   {target.Label}"
            : state.Remotes.Length == 0 ? Loc.Text("LD3C4D5FA05CE") : Loc.Text("L772AD8FF1B38");

        FetchRemote.ItemsSource = remotes;
        FetchRemote.SelectedItem = remotes.FirstOrDefault(remote => remote.Name == previousFetchRemote)
            ?? remotes.FirstOrDefault(remote => remote.Name == state.Upstream?.Remote)
            ?? (remotes.Length == 1 ? remotes[0] : null);
        UpdateFetchTooltip();

        PushSelectedItem.Items.Clear();
        if (state.Remotes.Length > 1)
        {
            foreach (GitDestination destination in state.AllPushTargets)
            {
                var singleDestination = destination with { SetUpstream = state.Upstream is null };
                var item = new MenuItem { Header = new TextBlock { Text = singleDestination.Label } };
                item.Click += async (_, _) => await PushSelectedAsync(singleDestination);
                PushSelectedItem.Items.Add(item);
            }
        }
        _ = LoadDiffAsync();
    }

    private GitFileChange? SelectedFile => StagedFiles.SelectedItem as GitFileChange
        ?? (WorkingFiles.SelectedItems.Count == 1 ? WorkingFiles.SelectedItem as GitFileChange : null);
    private GitDestination? SinglePushTarget => _state is { Remotes.Length: 1 } state &&
        state.AllPushTargets.FirstOrDefault() is { } target ? target with { SetUpstream = state.Upstream is null } : null;

    private void UpdateFileListLayout(int working, int staged)
    {
        UpdateFileSection(WorkingSection, WorkingFiles, WorkingEmpty, WorkingRow, working);
        UpdateFileSection(StagedSection, StagedFiles, StagedEmpty, StagedRow, staged);
        UpdateFileSection(StashSection, StashList, StashEmpty, StashRow, (StashList.ItemsSource as GitStash[])?.Length ?? 0);
    }

    private static void UpdateFileSection(ToggleButton section, ListBoxEdit files, TextBlock empty, RowDefinition row, int count)
    {
        bool expanded = section.IsChecked == true;
        bool showFiles = expanded && count > 0;
        files.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = expanded && count == 0 ? Visibility.Visible : Visibility.Collapsed;
        row.Height = showFiles ? new(1, GridUnitType.Star) : GridLength.Auto;
        row.MaxHeight = showFiles ? count * 31.0 + 8 : double.PositiveInfinity;
    }

    private void ToggleSectionClick(object sender, RoutedEventArgs e)
    {
        UpdateFileListLayout((WorkingFiles.ItemsSource as GitFileChange[])?.Length ?? 0,
            (StagedFiles.ItemsSource as GitFileChange[])?.Length ?? 0);
        UpdateActions();
    }

    private void UpdateActions()
    {
        if (CommitButton is null || PublishButton is null || FetchRemote is null || FileAction is null ||
            UnstageButton is null || CommitPushMenuItem is null || PublishMenuItem is null || StashList is null || DropStashButton is null)
            return;

        ChangeControls.IsEnabled = RefreshButton.IsEnabled = MoreButton.IsEnabled = !_busy;
        BranchButton.IsEnabled = !_busy && _state is not null;
        FetchRemote.IsEnabled = !_busy;
        FetchRemoteRow.Visibility = _state is { Remotes.Length: > 1 } ? Visibility.Visible : Visibility.Collapsed;
        FetchButton.IsEnabled = !_busy && _state is not null && FetchRemote.SelectedItem is GitRemote { Url: not null };
        PullButton.IsEnabled = !_busy && _state is { PullBlockReason: null };
        PullButton.ToolTip = _state?.PullBlockReason ??
            (_state?.Upstream is { } upstream ? Loc.Format("L111B1F90246F", upstream.Label) : Loc.Text("L79F165C0DAF3"));
        CommitButton.IsEnabled = !_busy && _state is { InProgress: null } state && state.Status.HeadRef is not null
            && (state.Status.Files ?? []).Any(file => file.IsStaged) && !(state.Status.Files ?? []).Any(file => file.IsConflict)
            && !string.IsNullOrWhiteSpace(CommitMessage.Text);
        var target = SinglePushTarget;
        bool multipleTargets = _state is { Remotes.Length: > 1, Status.HeadRef: not null };
        PushSelectedItem.IsEnabled = !_busy && multipleTargets && _state?.Status.CommitId is not null;
        PushSelectedItem.Visibility = multipleTargets ? Visibility.Visible : Visibility.Collapsed;
        PushButton.IsEnabled = !_busy && _state?.Status.CommitId is not null && (multipleTargets || target is not null);
        CommitMenuButton.IsEnabled = !_busy && _state is not null;
        CommitMenuItem.IsEnabled = CommitButton.IsEnabled;
        CommitPushMenuItem.IsEnabled = CommitButton.IsEnabled && (multipleTargets || target is not null);
        CommitPushMenuItem.Header = multipleTargets ? Loc.Text("L80ABB878D34D") : Loc.Text("L8418000C8F8E");
        CommitPushMenuItem.ToolTip = multipleTargets ? Loc.Text("L42B49AC0E97D")
            : target is null ? Loc.Text("L92BE97965B6C") : Loc.Format("L80D3CE579ACF", target.Label);
        PushButton.ToolTip = multipleTargets ? Loc.Text("L5952C33CAFA7")
            : target is null ? Loc.Text("L6D5B513A8C31") : Loc.Format("L80D3CE579ACF", target.Label);
        PublishButton.Visibility = _state is { Remotes.Length: 0 } ? Visibility.Visible : Visibility.Collapsed;
        AddRemoteItem.IsEnabled = !_busy && _state is not null;
        ManageRemotesItem.IsEnabled = !_busy && FetchRemote.ItemsSource is GitRemote[] { Length: > 0 };
        PublishMenuItem.IsEnabled = PublishButton.IsEnabled =
            !_busy && _state is { InProgress: null, Status: { HeadRef: not null, CommitId: not null } };
        PublishButton.ToolTip = PublishButton.IsEnabled ? Loc.Text("L96140899241A")
            : Loc.Text("L62C76915AB5E");
        DiffButton.IsEnabled = !_busy && SelectedFile is not null;
        int selectedCount = WorkingFiles.SelectedItems.Count;
        bool hasConflict = _state?.Status.Files?.Any(file => file.IsConflict) == true;
        StageAllButton.IsEnabled = !_busy && !hasConflict && WorkingFiles.ItemsSource is GitFileChange[] { Length: > 0 };
        StageAllButton.ToolTip = hasConflict ? Loc.Text("LD958837093BB")
            : Loc.Text("L313589EB624A");
        FileAction.IsEnabled = !_busy && _state is not null && WorkingSection.IsChecked == true && selectedCount > 0;
        FileAction.ToolTip = selectedCount > 0 ? Loc.Format("LB0047676997C", selectedCount) : Loc.Text("L0F72090B9A51");
        UnstageButton.IsEnabled = !_busy && _state is not null && StagedSection.IsChecked == true && StagedFiles.SelectedItem is not null;
        bool canUseStash = !_busy && _state is { InProgress: null, Status.CommitId: not null } && !hasConflict;
        SaveStashButton.IsEnabled = SaveStashMenuItem.IsEnabled = canUseStash && _state?.Status.Files is { Count: > 0 };
        SaveStashButton.ToolTip = SaveStashMenuItem.ToolTip = _state?.Status.CommitId is null
            ? Loc.Text("L5A36A8F47DC0")
            : hasConflict ? Loc.Text("L5CA7E4351C27")
            : Loc.Text("LC30054C8BE8D");
        ApplyStashButton.IsEnabled = canUseStash && StashSection.IsChecked == true && StashList.SelectedItem is GitStash;
        DropStashButton.IsEnabled = !_busy && StashSection.IsChecked == true && StashList.SelectedItem is GitStash;
        _owner.UpdateGitDiffNavigation();
    }

    private void DismissStatusClick(object sender, RoutedEventArgs e) => StatusMessage.Text = "";

    private void StashSelectionChanged(object sender, RoutedEventArgs e) => UpdateActions();

    private async void SaveStashClick(object sender, RoutedEventArgs e)
    {
        if (!SaveStashButton.IsEnabled || _state is not GitWorkbenchState state)
            return;

        await RunOperationAsync(Loc.Text("L3384A2FF589C"), async token =>
        {
            await _git.SaveStashAsync(state.Status, token);
            StashSection.IsChecked = true;
        }, Loc.Text("L3DF03DA5A78B"));
    }

    private async void ApplyStashClick(object sender, RoutedEventArgs e)
    {
        if (!ApplyStashButton.IsEnabled || _state is not GitWorkbenchState state ||
            StashList.SelectedItem is not GitStash stash)
            return;

        if (state.Status.Files is { Count: > 0 } &&
            MessageBox.Show(_owner, Loc.Format("LFA779E13313F", stash.Label),
                Loc.Text("LDA8F5BB18F35"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunOperationAsync(Loc.Text("L6D31F3C5E713"), token => _git.ApplyStashAsync(state.Status, stash, token),
            Loc.Text("L3F715CDDDD29"));
    }

    private async void DropStashClick(object sender, RoutedEventArgs e)
    {
        if (!DropStashButton.IsEnabled || StashList.SelectedItem is not GitStash stash)
            return;
        if (MessageBox.Show(_owner, Loc.Format("L723258E69CE7", stash.Label),
            Loc.Text("L2444EBC212EC"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunOperationAsync(Loc.Text("L1DE016D3491C"), token => _git.DropStashAsync(stash, token), Loc.Text("L81A9A2358258"));
    }

    private void CommitMessageChanged(object sender, EditValueChangedEventArgs e) => UpdateActions();

    private void FetchRemoteChanged(object sender, EditValueChangedEventArgs e)
    {
        UpdateFetchTooltip();
        UpdateActions();
    }

    private void UpdateFetchTooltip()
    {
        if (FetchButton is null || FetchRemote is null)
            return;

        FetchButton.ToolTip = FetchRemote.SelectedItem is GitRemote remote ? $"Fetch · {remote.Url ?? remote.Name}" :
            _state is { Remotes.Length: 0 } ? Loc.Text("L727BC3EB5607") : Loc.Text("L02AD0C61CB59");
        FetchRemote.ToolTip = FetchButton.ToolTip;
    }

    private async void FileSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_closing.IsCancellationRequested || _updatingLists || StagedFiles is null || WorkingFiles is null)
            return;
        _updatingLists = true;
        if (sender is ListBoxEdit { SelectedItem: not null } list)
            (ReferenceEquals(list, StagedFiles) ? WorkingFiles : StagedFiles).UnSelectAll();
        _updatingLists = false;
        UpdateActions();
        if (!_busy && SelectedFile is not null)
            _owner.ShowGitDiff();
        await LoadDiffAsync();
    }

    private async Task LoadDiffAsync()
    {
        _preview?.Cancel();
        if (SelectedFile is not GitFileChange file)
        {
            _owner.SetGitDiffHeader(Loc.Text("LC302D0999566"), "");
            _owner.SetGitDiffPreview(new([], Loc.Text("LF9DB2AD7BFF8")));
            return;
        }

        bool staged = StagedFiles.SelectedItem is not null;
        string subtitle = file.IsConflict ? Loc.Text("LEF179F25624C") : staged ? Loc.Text("L9AA6E08B4D78") : file.Index == '?' ? Loc.Text("L945D14235750") : Loc.Text("L89FC9C5A9F55");
        string before = file.IsConflict ? Loc.Text("L92EC3FD02ED1") : file.Index == '?' ? Loc.Text("LDE079D7609F1") :
            staged ? Loc.Text("LF2E7A2CC63E7") : Loc.Text("LCBDC303A4FC5");
        _owner.SetGitDiffHeader(file.DisplayPath, subtitle, before, staged ? Loc.Text("LAD80B9018BAB") : Loc.Text("L2B977A882240"));
        _owner.SetGitDiffPreview(new([], Loc.Text("LEB0339DA9707")));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _preview = request;
        try
        {
            GitDiffPreview diff = await GitFilePreview.ReadAsync(_repository, file, staged, request.Token);
            if (!request.IsCancellationRequested)
                _owner.SetGitDiffPreview(diff);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or DecoderFallbackException or ArgumentException)
        {
            if (!request.IsCancellationRequested)
                _owner.SetGitDiffPreview(new([], Loc.Text("LE96CFF9D2598") + ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_preview, request))
                _preview = null;
        }
    }

    private void DiffClick(object sender, RoutedEventArgs e) => _owner.ShowGitDiff();

    private void FileLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (!_busy && sender is FrameworkElement { DataContext: GitFileChange file } && ReferenceEquals(SelectedFile, file))
            _owner.ShowGitDiff();
    }

    private async void FileActionClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, UnstageButton) && StagedFiles.SelectedItem is GitFileChange file)
            await UnstageAsync(file);
        else if (ReferenceEquals(sender, FileAction))
            await StageAsync(WorkingFiles.SelectedItems.OfType<GitFileChange>().ToArray());
    }

    private async void StageAllClick(object sender, RoutedEventArgs e)
    {
        if (StageAllButton.IsEnabled && WorkingFiles.ItemsSource is GitFileChange[] files)
            await StageAsync(files);
    }

    private Task StageAsync(GitFileChange[] files)
    {
        if (_state is not GitWorkbenchState state || files.Length == 0)
            return Task.CompletedTask;

        return RunOperationAsync(Loc.Text("LFAFBF9F1DFAF"), token => _git.StageAsync(state.Status, files, token),
            files.Length == 1 ? Loc.Text("L842E0040DB62") : Loc.Format("LBC96D4A775D7", files.Length));
    }

    private Task UnstageAsync(GitFileChange file) => _state is GitWorkbenchState state
        ? RunOperationAsync(Loc.Text("L740B79D1AB39"), token => _git.UnstageAsync(state.Status, file, token), Loc.Text("LE10B8D68FDDE"))
        : Task.CompletedTask;

    private async void CommitClick(object sender, RoutedEventArgs e) => await CommitAsync(null);

    private async void CommitPushClick(object sender, RoutedEventArgs e)
    {
        if (!CommitPushMenuItem.IsEnabled || _state is not { } state)
            return;
        if (state.Remotes.Length > 1)
        {
            GitDestination[] targets = state.AllPushTargets;
            if (ConfirmPushAll(state.Status.Branch, targets, commit: true))
                await CommitAsync(null, targets);
            return;
        }
        if (SinglePushTarget is not { } target)
            return;

        if (MessageBox.Show(_owner, Loc.Format("LE7714B16F1E1", target.Label),
            Loc.Text("L8418000C8F8E"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await CommitAsync(target);
    }

    private async Task CommitAsync(GitDestination? target, GitDestination[]? allTargets = null)
    {
        if (!CommitButton.IsEnabled || _state is not GitWorkbenchState state)
            return;

        string message = CommitMessage.Text.Trim();
        GitPushResult[]? results = null;
        await RunOperationAsync(Loc.Text("LE34B6AF07D73"), async token =>
        {
            await _git.CommitAsync(state, message, token);
            CommitMessage.EditValue = null;
            if (target is not null || allTargets is not null)
            {
                StatusMessage.Text = Loc.Text("L5851AD7CCF60");
                try
                {
                    GitWorkbenchState afterCommit = await _git.ReadAsync(token);
                    if (afterCommit.Status.HeadRef != state.Status.HeadRef)
                        throw new IOException(Loc.Text("LE4AC3D574548"));
                    if (allTargets is not null)
                        results = await _git.PushAllAsync(afterCommit, allTargets, token);
                    else
                        await _git.PushAsync(afterCommit, target!, token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    throw new IOException(Loc.Format("LC7B74695CBA4", ex.Message), ex);
                }
            }
        }, target is null ? Loc.Text("L6371ABE6C4C5") : Loc.Format("LDF90000CC751", target.Label));
        if (results is not null)
            ShowPushResults(results, Loc.Text("L9695D9B77D08"));
    }

    private async void PublishClick(object sender, RoutedEventArgs e) => await PublishRepositoryAsync();

    private async Task PublishRepositoryAsync()
    {
        if (!PublishMenuItem.IsEnabled || _state is not GitWorkbenchState state)
            return;

        var dialog = new GitHubPublishWindow(_git, state.Status, Path.GetFileName(_repository)) { Owner = _owner };
        dialog.ShowDialog();
        if (dialog.RepositoryCreated)
        {
            await RefreshAsync();
            StatusMessage.Text = dialog.Published ? Loc.Format("LF01624E2345E", dialog.RepositoryUrl)
                : Loc.Format("L13B3703D161E", dialog.RepositoryUrl);
            _owner.GitRepositoryChanged();
        }
    }

    private async void ManageRemotesClick(object sender, RoutedEventArgs e)
    {
        if (_busy || FetchRemote.ItemsSource is not GitRemote[] { Length: > 0 } remotes)
            return;

        var dialog = new GitRemotesWindow(Project.Name, remotes, _state?.Upstream?.Remote) { Owner = _owner };
        if (dialog.ShowDialog() == true && dialog.RemoteToRemove is { } remote)
            await RunOperationAsync(Loc.Text("LAAD0F6C0D8C3"), token => _git.RemoveRemoteAsync(remote, token),
                Loc.Format("LC0537BEBB691", remote.Name));
    }

    private async void AddRemoteClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _state is not GitWorkbenchState state)
            return;

        var dialog = new GitRemoteWindow(state.Remotes, _owner.KnownGitRepositories(),
            FetchRemote.ItemsSource as GitRemote[] ?? [], Project.Name, PublishMenuItem.IsEnabled) { Owner = _owner };
        if (dialog.ShowDialog() != true)
            return;
        if (dialog.CreateGitHub)
        {
            await PublishRepositoryAsync();
            return;
        }

        await RunOperationAsync(Loc.Text("LD2FB245A12B5"),
            token => dialog.SelectedGitHub is { } repo ? _git.AddGitHubRemoteAsync(dialog.RemoteName, repo, token)
                : _git.AddRemoteAsync(dialog.RemoteName, dialog.RemoteUrl, token),
            Loc.Format("L3DC37272B0A1", dialog.RemoteName));
    }

    private async void PushClick(object sender, RoutedEventArgs e) => await PushAsync();

    private async Task PushAsync()
    {
        if (!PushButton.IsEnabled || _state is not GitWorkbenchState state)
            return;
        if (state.Remotes.Length < 2)
        {
            if (SinglePushTarget is { } target)
                await PushSelectedAsync(target);
            return;
        }

        GitDestination[] targets = state.AllPushTargets;
        if (!ConfirmPushAll(state.Status.Branch, targets, commit: false))
            return;

        GitPushResult[]? results = null;
        await RunOperationAsync(Loc.Text("LCD7FF0860F79"), async token =>
        {
            results = await _git.PushAllAsync(state, targets, token);
        });
        if (results is not null)
            ShowPushResults(results);
    }

    private bool ConfirmPushAll(string branch, GitDestination[] targets, bool commit)
    {
        string action = commit ? Loc.Text("L5753E18485A6")
            : Loc.Format("LA9D02707C63C", branch);
        return MessageBox.Show(_owner, $"{action}\n\n{string.Join(Environment.NewLine, targets.Select(target => target.Label))}",
            Loc.Text("L5952C33CAFA7"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void ShowPushResults(GitPushResult[] results, string prefix = "")
    {
        int succeeded = results.Count(result => result.Error is null);
        StatusMessage.Text = Loc.Format("LB254A7E4F0D4", prefix, succeeded, results.Length);
        string details = string.Join("\n\n", results.Select(result => $"{result.Target.Label} · {result.Error ?? Loc.Text("L59C1C2FCFD11")}"));
        MessageBox.Show(_owner, $"{StatusMessage.Text}\n\n{details}", Loc.Text("L7FCDE4D41A3B"), MessageBoxButton.OK,
            succeeded == results.Length ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task PushSelectedAsync(GitDestination target)
    {
        if (_busy || _state is not { Status.CommitId: not null } state ||
            !state.Remotes.Contains(target.Remote, StringComparer.Ordinal))
            return;

        string detail = target.SetUpstream ? Loc.Text("L7081361A8F22") : "";
        if (MessageBox.Show(_owner, Loc.Format("L8E71D234B121", state.Status.Branch, target.Label, detail),
            "Push", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunOperationAsync(Loc.Text("LAA6973CBECD8"), token => _git.PushAsync(state, target, token),
            Loc.Format("LF2B61C793BEC", target.Label));
    }

    private async void FetchClick(object sender, RoutedEventArgs e) => await FetchAsync();

    private async Task FetchAsync()
    {
        if (FetchButton.IsEnabled && FetchRemote.SelectedItem is GitRemote remote)
            await RunOperationAsync(Loc.Format("L0369CB536CF1", remote.Name), token => _git.FetchAsync(remote.Name, token),
                Loc.Format("L07339E5F7C2C", remote.Name));
    }

    private async void PullClick(object sender, RoutedEventArgs e) => await PullAsync();

    private async Task PullAsync()
    {
        if (!PullButton.IsEnabled || _state is not { Upstream: { } target } state)
            return;

        await RunOperationAsync(Loc.Format("L0369CB536CF1", target.Label), token => _git.PullAsync(state, token),
            Loc.Format("LDF5498949F61", target.Label));
    }

    internal Task RunRepositoryMenuCommandAsync(string command)
    {
        if (_busy)
            return Task.CompletedTask;

        switch (command)
        {
            case "Fetch" when FetchButton.IsEnabled: return FetchAsync();
            case "Pull" when PullButton.IsEnabled: return PullAsync();
            case "Push" when PushButton.IsEnabled: return PushAsync();
            default:
                StatusMessage.Text = Loc.Format("L4E5938A0CFC7", command);
                return Task.CompletedTask;
        }
    }

    private void HistoryClick(object sender, RoutedEventArgs e) => new GitHistoryWindow(Project) { Owner = _owner }.ShowDialog();

    private async void BranchMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || _state is not GitWorkbenchState state || _busy)
            return;

        var loading = new MenuItem { Header = Loc.Text("L65BA27E80F67"), IsEnabled = false };
        menu.Items.Clear();
        var createRemote = new MenuItem
        {
            Header = Loc.Text("LC9FC267E05FA"),
            IsEnabled = state.Remotes.Length > 0 && state.Status.CommitId is not null && state.InProgress is null,
            ToolTip = state.InProgress ?? (state.Remotes.Length == 0 ? Loc.Text("L576CFBFD2903")
                : state.Status.CommitId is null ? Loc.Text("L11826F639B84") : Loc.Text("LAD810CDB9D12"))
        };
        ToolTipService.SetShowOnDisabled(createRemote, true);
        createRemote.Click += CreateRemoteBranchClick;
        menu.Items.Add(createRemote);
        menu.Items.Add(new Separator());
        menu.Items.Add(loading);
        CancellationToken token = _closing.Token;
        try
        {
            GitReference[] refs = await _git.ReadReferencesAsync(token);
            if (!menu.IsOpen || !menu.Items.Contains(loading) || token.IsCancellationRequested)
                return;

            menu.Items.Remove(loading);
            string? blocked = state.InProgress ?? (state.Status.State == GitState.Clean ? null :
                Loc.Text("LE3E6117ABC4F"));
            if (blocked is not null)
                menu.Items.Add(new MenuItem { Header = blocked, IsEnabled = false });

            AddBranchChoices(menu, Loc.Text("L0C45D1237417"), refs.Where(item => item.IsLocalBranch), blocked, null, Loc.Text("L1B09EC835A1F"));
            menu.Items.Add(new Separator());
            AddBranchChoices(menu, Loc.Text("LF99FE0ADD7E4"), refs.Where(item => item.IsRemoteBranch), blocked,
                refs.FirstOrDefault(item => item.Current)?.Upstream,
                state.Remotes.Length > 0 ? Loc.Text("L605006F30A91") : Loc.Text("LF8A40EEE03A3"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            if (menu.Items.Contains(loading))
            {
                loading.Header = Loc.Text("L3814EA5447EF");
                loading.ToolTip = ex.Message;
                ToolTipService.SetShowOnDisabled(loading, true);
            }
        }
    }

    private async void CreateRemoteBranchClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _state is not { Remotes.Length: > 0, Status.CommitId: not null, InProgress: null } state)
            return;

        var dialog = new GitRemoteBranchWindow(state.Remotes, state.Upstream?.Remote, state.Status.Branch) { Owner = _owner };
        if (dialog.ShowDialog() == true)
            await RunOperationAsync(Loc.Text("LB46AD1C6CBE2"),
                token => _git.CreateRemoteBranchAsync(state.Status, dialog.Remote, dialog.Branch, token),
                Loc.Format("L8E4E7DAB08AE", dialog.Remote, dialog.Branch));
    }

    private void AddBranchChoices(ContextMenu menu, string title, IEnumerable<GitReference> branches,
        string? blocked, string? currentUpstream, string empty)
    {
        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        bool hasBranches = false;
        foreach (GitReference branch in branches)
        {
            hasBranches = true;
            bool current = branch.Current || branch.FullName == currentUpstream;
            var choice = new MenuItem
            {
                Header = new TextBlock { Text = branch.Name },
                IsCheckable = current,
                IsChecked = current,
                IsEnabled = !current && blocked is null
            };
            choice.Click += async (_, _) => await RunOperationAsync(Loc.Text("LF047A226FEDC"),
                token => _git.SwitchBranchAsync(branch, token), Loc.Text("L5C0234F0A896"));
            menu.Items.Add(choice);
        }
        if (!hasBranches)
            menu.Items.Add(new MenuItem { Header = empty, IsEnabled = false });
    }

    private async void ReferencesClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var window = new GitReferencesWindow(Project) { Owner = _owner };
        window.ShowDialog();
        if (window.RepositoryChanged)
        {
            await RefreshAsync();
            _owner.GitRepositoryChanged();
        }
    }

    private void OpenActionMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void FileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { DataContext: GitFileChange file } row)
            return;

        var list = FindParent<ListBoxEdit>(row);
        if (list is null)
            return;

        e.Handled = true;
        if (!list.SelectedItems.Contains(file))
        {
            list.UnSelectAll();
            list.SelectedItem = file;
        }
        ShowFileMenu(row, file, ReferenceEquals(list, StagedFiles), PlacementMode.MousePoint);
    }

    private static T? FindParent<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? parent = element; parent is not null; parent = System.Windows.Media.VisualTreeHelper.GetParent(parent))
            if (parent is T match)
                return match;
        return null;
    }

    private void FileMenuKey(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!_busy && ReferenceEquals(sender, WorkingFiles) && key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            WorkingFiles.SelectAll();
            return;
        }
        if (_busy || sender is not ListBoxEdit { SelectedItem: GitFileChange file } list ||
            (key != Key.Apps && (key != Key.F10 || Keyboard.Modifiers != ModifierKeys.Shift)))
            return;

        e.Handled = true;
        ShowFileMenu(list, file, ReferenceEquals(list, StagedFiles), PlacementMode.Bottom);
    }

    private void ShowFileMenu(FrameworkElement item, GitFileChange file, bool staged, PlacementMode placement)
    {
        string path = Path.GetFullPath(file.Path, _repository);
        var menu = new ContextMenu { PlacementTarget = item, Placement = placement };
        var selected = staged ? [file] : WorkingFiles.SelectedItems.OfType<GitFileChange>().ToArray();
        var change = new MenuItem
        {
            Header = staged ? Loc.Text("LA9899DD9E891") : selected.Length > 1 ? Loc.Format("LB0047676997C", selected.Length) : Loc.Text("L08088D0569FD"),
            IsEnabled = _state is not null
        };
        change.Click += async (_, _) => await (staged ? UnstageAsync(file) : StageAsync(selected));
        var open = new MenuItem { Header = Loc.Text("L9580C0E81F1F"), IsEnabled = File.Exists(path) };
        open.Click += (_, _) => RunFileAction(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        string folder = Path.GetDirectoryName(path) ?? _repository;
        var openFolder = new MenuItem { Header = Loc.Text("LD6F2F6CAD078"), IsEnabled = Directory.Exists(folder) };
        openFolder.Click += (_, _) => RunFileAction(() => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }));
        var copy = new MenuItem { Header = Loc.Text("L23F0ACF72016") };
        copy.Click += (_, _) => RunFileAction(() => Clipboard.SetText(path));
        menu.Items.Add(change);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(openFolder);
        menu.Items.Add(copy);
        menu.IsOpen = true;
    }

    private void RunFileAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ExternalException)
        {
            StatusMessage.Text = ex.Message;
        }
    }
}
