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

    internal GitWorkbenchView(MainWindow owner, ProjectEntry project)
    {
        _owner = owner;
        Project = project;
        _repository = project.Repository ?? throw new ArgumentException("Git deposu bulunamadı.");
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
    internal Task RefreshAsync() => RunOperationAsync("Depo okunuyor…");

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
                    operation?.Finish("Tamamlandı", success);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    result = ex.Message;
                    operation?.Finish("Başarısız", result);
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
                StatusMessage.Text = "Dosyayı seçerek farkı inceleyebilir veya sağ tıklayarak işlem yapabilirsin.";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            if (operation is { Status: "Çalışıyor" })
                operation.Finish("Durduruldu");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _state = null;
            StagedFiles.ItemsSource = WorkingFiles.ItemsSource = null;
            StashList.ItemsSource = null;
            StashTitle.Text = "Geçici kayıtlar";
            StashEmpty.Text = "Geçici kayıtlar okunamadı.";
            StashEmpty.ToolTip = ex.Message;
            BranchName.Text = "Dal okunamadı";
            BranchStatus.Text = "";
            StagedTitle.Text = "Hazırlananlar";
            WorkingTitle.Text = "Değişiklikler";
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
            StashTitle.Text = $"Geçici kayıtlar ({stashes.Length})";
            StashEmpty.Text = "Geçici kayıt yok.";
            StashEmpty.ToolTip = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            StashList.ItemsSource = null;
            StashTitle.Text = "Geçici kayıtlar";
            StashEmpty.Text = "Geçici kayıtlar okunamadı.";
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
        StagedTitle.Text = $"Hazırlananlar ({staged.Length})";
        WorkingTitle.Text = $"Değişiklikler ({working.Length})";
        UpdateFileListLayout(working.Length, staged.Length);
        BranchName.Text = state.Status.Branch;
        BranchStatus.Text = state.Upstream is { } target
            ? $"{state.Status.Tracking}   ·   {target.Label}"
            : state.Remotes.Length == 0 ? "Uzak depo yok · … menüsünden ekleyin" : "Takip edilen uzak dal yok";

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
            (_state?.Upstream is { } upstream ? $"Hedef {upstream.Label} · Yalnızca fast-forward" : "Depo durumu okunuyor.");
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
        CommitPushMenuItem.Header = multipleTargets ? "Commit et ve tüm hedeflere gönder" : "Commit et ve gönder";
        CommitPushMenuItem.ToolTip = multipleTargets ? "Commit’i bağlı tüm uzak depolara gönder."
            : target is null ? "Push için bir uzak depo gerekli." : $"Hedef {target.Label}";
        PushButton.ToolTip = multipleTargets ? "Tüm uzak depolara gönder"
            : target is null ? "Push için uzak depo ve hedef dal gerekli." : $"Hedef {target.Label}";
        PublishButton.Visibility = _state is { Remotes.Length: 0 } ? Visibility.Visible : Visibility.Collapsed;
        AddRemoteItem.IsEnabled = !_busy && _state is not null;
        ManageRemotesItem.IsEnabled = !_busy && FetchRemote.ItemsSource is GitRemote[] { Length: > 0 };
        PublishMenuItem.IsEnabled = PublishButton.IsEnabled =
            !_busy && _state is { InProgress: null, Status: { HeadRef: not null, CommitId: not null } };
        PublishButton.ToolTip = PublishButton.IsEnabled ? "Yeni bir GitHub deposu oluştur ve etkin dalı gönder."
            : "Yayımlamak için commit içeren bir dal ve tamamlanmış Git işlemleri gerekli.";
        DiffButton.IsEnabled = !_busy && SelectedFile is not null;
        int selectedCount = WorkingFiles.SelectedItems.Count;
        bool hasConflict = _state?.Status.Files?.Any(file => file.IsConflict) == true;
        StageAllButton.IsEnabled = !_busy && !hasConflict && WorkingFiles.ItemsSource is GitFileChange[] { Length: > 0 };
        StageAllButton.ToolTip = hasConflict ? "Tümünü hazırlamadan önce çakışmaları çözün."
            : "Listedeki tüm değişiklikleri commit için hazırla.";
        FileAction.IsEnabled = !_busy && _state is not null && WorkingSection.IsChecked == true && selectedCount > 0;
        FileAction.ToolTip = selectedCount > 0 ? $"Seçilileri hazırla ({selectedCount})" : "Seçilileri hazırla";
        UnstageButton.IsEnabled = !_busy && _state is not null && StagedSection.IsChecked == true && StagedFiles.SelectedItem is not null;
        bool canUseStash = !_busy && _state is { InProgress: null, Status.CommitId: not null } && !hasConflict;
        SaveStashButton.IsEnabled = SaveStashMenuItem.IsEnabled = canUseStash && _state?.Status.Files is { Count: > 0 };
        SaveStashButton.ToolTip = SaveStashMenuItem.ToolTip = _state?.Status.CommitId is null
            ? "Geçici kayıt için önce ilk commit’i oluşturun."
            : hasConflict ? "Geçici kayıttan önce çakışmaları çözün."
            : "Hazırlanan, hazırlanmamış ve yeni dosyaları geçici kaydet.";
        ApplyStashButton.IsEnabled = canUseStash && StashSection.IsChecked == true && StashList.SelectedItem is GitStash;
        DropStashButton.IsEnabled = !_busy && StashSection.IsChecked == true && StashList.SelectedItem is GitStash;
    }

    private void DismissStatusClick(object sender, RoutedEventArgs e) => StatusMessage.Text = "";

    private void StashSelectionChanged(object sender, RoutedEventArgs e) => UpdateActions();

    private async void SaveStashClick(object sender, RoutedEventArgs e)
    {
        if (!SaveStashButton.IsEnabled || _state is not GitWorkbenchState state)
            return;

        await RunOperationAsync("Değişiklikler geçici kaydediliyor…", async token =>
        {
            await _git.SaveStashAsync(state.Status, token);
            StashSection.IsChecked = true;
        }, "Değişiklikler geçici kaydedildi.");
    }

    private async void ApplyStashClick(object sender, RoutedEventArgs e)
    {
        if (!ApplyStashButton.IsEnabled || _state is not GitWorkbenchState state ||
            StashList.SelectedItem is not GitStash stash)
            return;

        if (state.Status.Files is { Count: > 0 } &&
            MessageBox.Show(_owner, $"Bu kayıt mevcut çalışma dosyalarına uygulanacak. Devam edilsin mi?\n\n{stash.Label}",
                "Geçici kaydı uygula", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunOperationAsync("Geçici kayıt uygulanıyor…", token => _git.ApplyStashAsync(state.Status, stash, token),
            "Değişiklikler geri uygulandı. Geçici kayıt listede duruyor.");
    }

    private async void DropStashClick(object sender, RoutedEventArgs e)
    {
        if (!DropStashButton.IsEnabled || StashList.SelectedItem is not GitStash stash)
            return;
        if (MessageBox.Show(_owner, $"{stash.Label}\n\nBu geçici kayıt kalıcı olarak silinsin mi? Çalışma dosyaları korunur.",
            "Geçici kaydı sil", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunOperationAsync("Geçici kayıt siliniyor…", token => _git.DropStashAsync(stash, token), "Seçili geçici kayıt silindi.");
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
            _state is { Remotes.Length: 0 } ? "Fetch · Uzak depo tanımlı değil" : "Fetch · Uzak depo seçin";
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
            _owner.SetGitDiffHeader("Dosya farkı", "");
            _owner.SetGitDiffPreview(new([], "Farkı görmek için listeden bir dosya seçin."));
            return;
        }

        bool staged = StagedFiles.SelectedItem is not null;
        string subtitle = file.IsConflict ? "Çakışma" : staged ? "Hazırlanan fark" : file.Index == '?' ? "İzlenmeyen dosya" : "Çalışma ağacındaki fark";
        string before = file.IsConflict ? "Bizim sürüm" : file.Index == '?' ? "Önce · Dosya yok" :
            staged ? "Önce · HEAD" : "Önce · Git indeksi";
        _owner.SetGitDiffHeader(file.DisplayPath, subtitle, before, staged ? "Sonra · Hazırlanan sürüm" : "Sonra · Çalışma dosyası");
        _owner.SetGitDiffPreview(new([], "Önizleme yükleniyor…"));
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
                _owner.SetGitDiffPreview(new([], "Dosya önizlemesi açılamadı. " + ex.Message));
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

        return RunOperationAsync("Değişiklikler hazırlanıyor…", token => _git.StageAsync(state.Status, files, token),
            files.Length == 1 ? "Dosya commit için hazırlandı." : $"{files.Length} dosya commit için hazırlandı.");
    }

    private Task UnstageAsync(GitFileChange file) => _state is GitWorkbenchState state
        ? RunOperationAsync("Hazırlama kaldırılıyor…", token => _git.UnstageAsync(state.Status, file, token), "Dosyanın hazırlaması kaldırıldı.")
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

        if (MessageBox.Show(_owner, $"Hazırlanan değişiklikler commit edilip {target.Label} hedefine gönderilsin mi?",
            "Commit et ve gönder", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await CommitAsync(target);
    }

    private async Task CommitAsync(GitDestination? target, GitDestination[]? allTargets = null)
    {
        if (!CommitButton.IsEnabled || _state is not GitWorkbenchState state)
            return;

        string message = CommitMessage.Text.Trim();
        GitPushResult[]? results = null;
        await RunOperationAsync("Commit oluşturuluyor…", async token =>
        {
            await _git.CommitAsync(state, message, token);
            CommitMessage.EditValue = null;
            if (target is not null || allTargets is not null)
            {
                StatusMessage.Text = "Commit kaydedildi. Uzak depoya gönderiliyor…";
                try
                {
                    GitWorkbenchState afterCommit = await _git.ReadAsync(token);
                    if (afterCommit.Status.HeadRef != state.Status.HeadRef)
                        throw new IOException("Etkin dal değişti. Push hedefini kontrol edin.");
                    if (allTargets is not null)
                        results = await _git.PushAllAsync(afterCommit, allTargets, token);
                    else
                        await _git.PushAsync(afterCommit, target!, token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    throw new IOException($"Commit kaydedildi. Push tamamlanmadı. {ex.Message}", ex);
                }
            }
        }, target is null ? "Commit yerel depoya kaydedildi." : $"Commit gönderildi. Hedef {target.Label}.");
        if (results is not null)
            ShowPushResults(results, "Commit yerel depoya kaydedildi. ");
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
            StatusMessage.Text = dialog.Published ? $"GitHub’a gönderildi. {dialog.RepositoryUrl}"
                : $"GitHub deposu oluşturuldu. {dialog.RepositoryUrl} · İlk push tamamlanmadı.";
            _owner.GitRepositoryChanged();
        }
    }

    private async void ManageRemotesClick(object sender, RoutedEventArgs e)
    {
        if (_busy || FetchRemote.ItemsSource is not GitRemote[] { Length: > 0 } remotes)
            return;

        var dialog = new GitRemotesWindow(Project.Name, remotes, _state?.Upstream?.Remote) { Owner = _owner };
        if (dialog.ShowDialog() == true && dialog.RemoteToRemove is { } remote)
            await RunOperationAsync("Uzak depo bağlantısı kaldırılıyor…", token => _git.RemoveRemoteAsync(remote, token),
                $"{remote.Name} bağlantısı kaldırıldı.");
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

        await RunOperationAsync("Uzak depo ekleniyor…",
            token => dialog.SelectedGitHub is { } repo ? _git.AddGitHubRemoteAsync(dialog.RemoteName, repo, token)
                : _git.AddRemoteAsync(dialog.RemoteName, dialog.RemoteUrl, token),
            $"Uzak depo eklendi. {dialog.RemoteName}");
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
        await RunOperationAsync("Commit’ler tüm uzak depolara gönderiliyor…", async token =>
        {
            results = await _git.PushAllAsync(state, targets, token);
        });
        if (results is not null)
            ShowPushResults(results);
    }

    private bool ConfirmPushAll(string branch, GitDestination[] targets, bool commit)
    {
        string action = commit ? "Hazırlanan değişiklikler commit edilip aşağıdaki hedeflere gönderilsin mi?"
            : $"{branch} dalındaki commit’ler aşağıdaki hedeflere gönderilsin mi?";
        return MessageBox.Show(_owner, $"{action}\n\n{string.Join(Environment.NewLine, targets.Select(target => target.Label))}",
            "Tüm uzak depolara gönder", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void ShowPushResults(GitPushResult[] results, string prefix = "")
    {
        int succeeded = results.Count(result => result.Error is null);
        StatusMessage.Text = $"{prefix}{succeeded}/{results.Length} uzak depoda push başarılı.";
        string details = string.Join("\n\n", results.Select(result => $"{result.Target.Label} · {result.Error ?? "Başarılı"}"));
        MessageBox.Show(_owner, $"{StatusMessage.Text}\n\n{details}", "Push sonuçları", MessageBoxButton.OK,
            succeeded == results.Length ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async Task PushSelectedAsync(GitDestination target)
    {
        if (_busy || _state is not { Status.CommitId: not null } state ||
            !state.Remotes.Contains(target.Remote, StringComparer.Ordinal))
            return;

        string detail = target.SetUpstream ? "\nİlk push ile bu dal takip edilmeye başlanacak." : "";
        if (MessageBox.Show(_owner, $"{state.Status.Branch} dalındaki commit’ler {target.Label} hedefine gönderilsin mi?{detail}",
            "Push", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await RunOperationAsync("Commit’ler uzak depoya gönderiliyor…", token => _git.PushAsync(state, target, token),
            $"Push tamamlandı. Hedef {target.Label}.");
    }

    private async void FetchClick(object sender, RoutedEventArgs e) => await FetchAsync();

    private async Task FetchAsync()
    {
        if (FetchButton.IsEnabled && FetchRemote.SelectedItem is GitRemote remote)
            await RunOperationAsync($"{remote.Name} güncellemeleri alınıyor…", token => _git.FetchAsync(remote.Name, token),
                $"Fetch tamamlandı. Uzak depo {remote.Name}.");
    }

    private async void PullClick(object sender, RoutedEventArgs e) => await PullAsync();

    private async Task PullAsync()
    {
        if (!PullButton.IsEnabled || _state is not { Upstream: { } target } state)
            return;

        await RunOperationAsync($"{target.Label} güncellemeleri alınıyor…", token => _git.PullAsync(state, token),
            $"Pull tamamlandı. Takip edilen dal {target.Label}.");
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
                StatusMessage.Text = $"Bu depo için {command} şu anda kullanılamıyor. Uzak depo ve dal ayarlarını kontrol edin.";
                return Task.CompletedTask;
        }
    }

    private void HistoryClick(object sender, RoutedEventArgs e) => new GitHistoryWindow(Project) { Owner = _owner }.ShowDialog();

    private async void BranchMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || _state is not GitWorkbenchState state || _busy)
            return;

        var loading = new MenuItem { Header = "Dallar okunuyor…", IsEnabled = false };
        menu.Items.Clear();
        var createRemote = new MenuItem
        {
            Header = "Uzak dal oluştur…",
            IsEnabled = state.Remotes.Length > 0 && state.Status.CommitId is not null && state.InProgress is null,
            ToolTip = state.InProgress ?? (state.Remotes.Length == 0 ? "Önce ⋯ menüsünden bir uzak depo ekleyin."
                : state.Status.CommitId is null ? "Önce bir commit oluşturun." : "Son commit’ten yeni bir uzak dal oluştur.")
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
                "Dal değiştirmeden önce commit edin veya geçici kaydedin.");
            if (blocked is not null)
                menu.Items.Add(new MenuItem { Header = blocked, IsEnabled = false });

            AddBranchChoices(menu, "Yerel dallar", refs.Where(item => item.IsLocalBranch), blocked, null, "Yerel dal yok.");
            menu.Items.Add(new Separator());
            AddBranchChoices(menu, "Uzak dallar", refs.Where(item => item.IsRemoteBranch), blocked,
                refs.FirstOrDefault(item => item.Current)?.Upstream,
                state.Remotes.Length > 0 ? "Uzak dalları almak için Fetch kullanın." : "Uzak depo tanımlı değil.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            if (menu.Items.Contains(loading))
            {
                loading.Header = "Dallar okunamadı";
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
            await RunOperationAsync("Uzak dal oluşturuluyor…",
                token => _git.CreateRemoteBranchAsync(state.Status, dialog.Remote, dialog.Branch, token),
                $"Uzak dal oluşturuldu · {dialog.Remote}/{dialog.Branch}");
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
            choice.Click += async (_, _) => await RunOperationAsync("Dal değiştiriliyor…",
                token => _git.SwitchBranchAsync(branch, token), "Etkin dal güncellendi.");
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
            Header = staged ? "Hazırlamayı kaldır" : selected.Length > 1 ? $"Seçilileri hazırla ({selected.Length})" : "Commit için hazırla",
            IsEnabled = _state is not null
        };
        change.Click += async (_, _) => await (staged ? UnstageAsync(file) : StageAsync(selected));
        var open = new MenuItem { Header = "Dosyayı aç", IsEnabled = File.Exists(path) };
        open.Click += (_, _) => RunFileAction(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        string folder = Path.GetDirectoryName(path) ?? _repository;
        var openFolder = new MenuItem { Header = "Klasörü aç", IsEnabled = Directory.Exists(folder) };
        openFolder.Click += (_, _) => RunFileAction(() => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }));
        var copy = new MenuItem { Header = "Dosya yolunu kopyala" };
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
