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
        RepositoryPath.Text = _repository;
        RepositoryPath.ToolTip = _repository;
    }

    internal void Release()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    internal Task RefreshAsync() => RunOperationAsync("Depo okunuyor…");

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
        try
        {
            if (action is not null)
            {
                try
                {
                    await action(_closing.Token);
                    result = success;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    result = ex.Message;
                }
            }

            GitWorkbenchState state = await _git.ReadAsync(_closing.Token);
            GitRemote[] remotes = await Task.WhenAll(state.Remotes.Select(remote => _git.ReadRemoteAsync(remote, _closing.Token)));
            LoadState(state, remotes);
            StatusMessage.Text = string.IsNullOrEmpty(result)
                ? _state!.InProgress ?? _state.Status.Detail
                : result;
            if (string.IsNullOrWhiteSpace(StatusMessage.Text))
                StatusMessage.Text = "Dosyayı seçerek farkı inceleyebilir veya sağ tıklayarak işlem yapabilirsin.";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _state = null;
            StagedFiles.ItemsSource = WorkingFiles.ItemsSource = null;
            BranchStatus.Text = "Depo okunamadı";
            StagedTitle.Text = "Hazırlananlar";
            WorkingTitle.Text = "Değişiklikler";
            UpdateFileListLayout(0, 0);
            PushRemote.Visibility = Visibility.Collapsed;
            FetchRemote.ItemsSource = null;
            UpdateFetchUrl();
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

    private void LoadState(GitWorkbenchState state, GitRemote[] remotes)
    {
        var selected = SelectedFile;
        bool wasStaged = StagedFiles.SelectedItem is not null;
        string? previousRemote = (PushRemote.SelectedItem as GitDestination)?.Remote;
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
        BranchStatus.Text = $"{state.Status.Branch}   ·   {state.Status.Tracking}";
        BranchStatus.Text += state.Upstream is { } target ? $"   ·   Takip {target.Label}" : "   ·   Takip edilen uzak dal yok";

        FetchRemote.ItemsSource = remotes;
        FetchRemote.SelectedItem = remotes.FirstOrDefault(remote => remote.Name == previousFetchRemote)
            ?? remotes.FirstOrDefault(remote => remote.Name == state.Upstream?.Remote)
            ?? (remotes.Length == 1 ? remotes[0] : null);
        UpdateFetchUrl();

        GitDestination[] choices = state.Upstream is null && state.Status.HeadRef is string head
            ? state.Remotes.Select(remote => new GitDestination(remote, head, true)).ToArray() : [];
        PushRemote.ItemsSource = choices;
        PushRemote.SelectedItem = choices.FirstOrDefault(choice => choice.Remote == previousRemote)
            ?? (choices.Length == 1 ? choices[0] : null);
        PushRemote.Visibility = choices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadDiffAsync();
    }

    private GitFileChange? SelectedFile => StagedFiles.SelectedItem as GitFileChange
        ?? (WorkingFiles.SelectedItems.Count == 1 ? WorkingFiles.SelectedItem as GitFileChange : null);
    private GitDestination? PushTarget => _state?.Upstream ?? PushRemote.SelectedItem as GitDestination;

    private void UpdateFileListLayout(int working, int staged)
    {
        WorkingFiles.Visibility = working > 0 ? Visibility.Visible : Visibility.Collapsed;
        StagedFiles.Visibility = staged > 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkingRow.Height = working > 0 ? new(1, GridUnitType.Star) : GridLength.Auto;
        StagedRow.Height = staged > 0 ? new(1, GridUnitType.Star) : GridLength.Auto;
    }

    private void UpdateActions()
    {
        if (CommitButton is null || PublishButton is null || FetchRemote is null || FileAction is null)
            return;

        ChangeControls.IsEnabled = RefreshButton.IsEnabled = HistoryButton.IsEnabled = !_busy;
        PushRemote.IsEnabled = !_busy;
        FetchRemote.IsEnabled = !_busy && _state is { Remotes.Length: > 0 };
        FetchButton.IsEnabled = !_busy && _state is not null && FetchRemote.SelectedItem is GitRemote { Url: not null };
        PullButton.IsEnabled = !_busy && _state is { PullBlockReason: null };
        PullButton.ToolTip = _state?.PullBlockReason ??
            (_state?.Upstream is { } upstream ? $"Hedef {upstream.Label} · Yalnızca fast-forward" : "Depo durumu okunuyor.");
        CommitButton.IsEnabled = !_busy && _state is { InProgress: null } state && state.Status.HeadRef is not null
            && (state.Status.Files ?? []).Any(file => file.IsStaged) && !(state.Status.Files ?? []).Any(file => file.IsConflict)
            && !string.IsNullOrWhiteSpace(CommitMessage.Text);
        var target = PushTarget;
        PushButton.IsEnabled = !_busy && _state?.Status.CommitId is not null && target is not null
            && _state.Remotes.Contains(target.Remote, StringComparer.Ordinal);
        PushButton.ToolTip = target is null ? "Push için uzak depo ve hedef dal gerekli." : $"Hedef {target.Label}";
        PublishButton.Visibility = _state is { Remotes.Length: 0 } ? Visibility.Visible : Visibility.Collapsed;
        PublishButton.IsEnabled = !_busy && _state is { Remotes.Length: 0, InProgress: null, Status: { HeadRef: not null, CommitId: not null } };
        PublishButton.ToolTip = PublishButton.IsEnabled ? "Yeni bir GitHub deposu oluştur ve etkin dalı gönder."
            : "Yayımlamak için commit içeren bir dal ve tamamlanmış Git işlemleri gerekli.";
        DiffButton.IsEnabled = !_busy && SelectedFile is not null;
        int selectedCount = WorkingFiles.SelectedItems.Count;
        bool hasConflict = _state?.Status.Files?.Any(file => file.IsConflict) == true;
        StageAllButton.IsEnabled = !_busy && !hasConflict && WorkingFiles.ItemsSource is GitFileChange[] { Length: > 0 };
        StageAllButton.ToolTip = hasConflict ? "Tümünü hazırlamadan önce çakışmaları çözün."
            : "Listedeki tüm değişiklikleri commit için hazırla.";
        FileAction.IsEnabled = !_busy && _state is not null && (StagedFiles.SelectedItem is not null || selectedCount > 0);
        FileAction.Content = StagedFiles.SelectedItem is not null ? "Hazırlamayı kaldır"
            : selectedCount > 1 ? $"Seçilileri hazırla ({selectedCount})" : "Hazırla";
    }

    private void CommitMessageChanged(object sender, EditValueChangedEventArgs e) => UpdateActions();
    private void PushRemoteChanged(object sender, EditValueChangedEventArgs e) => UpdateActions();

    private void FetchRemoteChanged(object sender, EditValueChangedEventArgs e)
    {
        UpdateFetchUrl();
        UpdateActions();
    }

    private void UpdateFetchUrl()
    {
        if (FetchUrl is null)
            return;

        FetchUrl.Text = FetchRemote.SelectedItem is GitRemote remote ? remote.Url ?? "Adres bulunamadı" :
            _state is null ? "" : _state.Remotes.Length == 0 ? "Uzak depo tanımlı değil" : "Fetch için uzak depo seçin";
        FetchUrl.ToolTip = FetchUrl.Text;
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
        if (StagedFiles.SelectedItem is GitFileChange file)
            await UnstageAsync(file);
        else
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

    private async void CommitClick(object sender, RoutedEventArgs e)
    {
        if (!CommitButton.IsEnabled || _state is not GitWorkbenchState state)
            return;

        string message = CommitMessage.Text.Trim();
        await RunOperationAsync("Commit oluşturuluyor…", async token =>
        {
            await _git.CommitAsync(state, message, token);
            CommitMessage.EditValue = null;
        }, "Commit yerel depoya kaydedildi.");
    }

    private async void PublishClick(object sender, RoutedEventArgs e)
    {
        if (!PublishButton.IsEnabled || _state is not GitWorkbenchState state)
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

    private async void PushClick(object sender, RoutedEventArgs e) => await PushAsync();

    private async Task PushAsync()
    {
        if (!PushButton.IsEnabled || _state is not GitWorkbenchState state || PushTarget is not GitDestination target)
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
