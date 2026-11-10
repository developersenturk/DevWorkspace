using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Editors;
using DevExpress.Xpf.Grid;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DevWorkspace;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Workspace? _workspace;
    private IReadOnlyList<ProjectEntry> _projects = [];
    private IReadOnlyList<WorkspaceFolder> _folders = [];
    private bool _buildingNavigation;
    private bool _busy;
    private bool _startupLayoutRestored;

    internal Task StartupReady => _startupReady.Task;

    public MainWindow()
    {
        InitializeComponent();
        _defaultProjectColumnWidths = ProjectColumns.Select(column => column.Width).ToArray();
        InitializeWorkspaceRefresh();
        RestoreLayout();
        WatchProjectColumnWidths();
        BuildNavigation("all");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_closing.IsCancellationRequested)
                return;

            _defaultDockLayout = CaptureDockLayout();
            RestoreDockLayout();
            _startupLayoutRestored = true;
            string[] args = Environment.GetCommandLineArgs();
            string? root = args.Length == 2 ? args[1] : _session.LastWorkspace;
            if (root is not null && (args.Length == 2 || Directory.Exists(root)))
                await OpenWorkspaceAsync(root);
            _startupReady.TrySetResult();
        }
        catch (Exception ex)
        {
            _startupReady.TrySetException(ex);
        }
    }

    internal void ShowStartupError(string message) => StatusText.Text = $"Çalışma alanı hazırlanamadı. {message}";

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = _gitView?.IsBusy == true || _savingExplorerFolder;
        if (!e.Cancel && _startupLayoutRestored && _startupReady.Task.IsCompleted)
        {
            CaptureLayout();
            CaptureWorkspaceView();
            _session.Save();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _startupReady.TrySetCanceled();
        StopWatchingProjectColumnWidths();
        if (Application.Current is { } app)
            app.Activated -= WorkspaceActivated;
        _gitView?.Release();
        _closing.Cancel();
        StopWorkspaceRefresh();
    }

    private bool IsMainMenuOpen => MainMenu.Items.OfType<MenuItem>().Any(item => item.IsSubmenuOpen);

    private async void ChooseWorkspaceClick(object sender, RoutedEventArgs e) => await ChooseWorkspaceAsync();

    private async Task ChooseWorkspaceAsync()
    {
        if (_busy)
            return;

        var picker = new OpenFolderDialog { Title = "Taranacak proje veya çalışma alanı klasörünü seçin" };
        if (_workspace is { } workspace && Directory.Exists(workspace.Root))
            picker.InitialDirectory = workspace.Root;

        if (picker.ShowDialog(this) == true)
            await OpenWorkspaceAsync(picker.FolderName);
    }

    private void FocusSearchClick(object sender, RoutedEventArgs e) => FocusSearch();

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void NavigateFromMenu(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string key } && _navigationNodes.TryGetValue(key, out var item))
            Navigation.SelectedItem = item;
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void ExitClick(object sender, RoutedEventArgs e) => Close();

    private async void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsMainMenuOpen)
            return;

        if (e.Key == Key.Escape && _explorerEdit is not null && _explorerContextMenu?.IsOpen != true)
        {
            e.Handled = true;
            CancelExplorerEdit();
        }
        else if (e.Key == Key.Enter && FindInlineExplorerEditor(e.OriginalSource) is { } editor)
        {
            e.Handled = true;
            await SaveExplorerEditAsync(editor.Text);
        }
        else if (e.Key == Key.Escape && _drawerProject is not null &&
            _projectContextMenu?.IsOpen != true)
        {
            e.Handled = true;
            CloseProjectDrawer();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        {
            e.Handled = true;
            QuickOpen();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            FocusSearch();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O && !_busy)
        {
            e.Handled = true;
            await ChooseWorkspaceAsync();
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        if (_workspace is { } workspace && !_busy)
            await OpenWorkspaceAsync(workspace.Root);
    }

    private async Task OpenWorkspaceAsync(string root, bool background = false)
    {
        Workspace? requestedWorkspace = _workspace;
        if (_busy || _savingExplorerFolder || (background && (_backgroundWorkspaceScan || requestedWorkspace is null)))
            return;

        if (_gitView?.IsBusy == true && !string.Equals(root, _workspace?.Root, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Çalışma alanını değiştirmeden önce Git işleminin bitmesini bekleyin.";
            return;
        }
        if (!Path.IsPathFullyQualified(root))
        {
            StatusText.Text = "Sürücü harfi veya ağ yolu dahil tam klasör yolunu yazın.";
            return;
        }

        if (background)
        {
            _backgroundWorkspaceScan = true;
        }
        else
        {
            SetBusy(true);
            StatusText.Text = "Çalışma alanı ve Git depoları taranıyor…";
        }
        try
        {
            CancellationToken token = _closing.Token;
            GitRecovery? recovery = RecoveryInventory;
            var (workspace, result, recoveryWarning) = await Task.Run(async () =>
            {
                Workspace opened = Workspace.Open(root);
                ScanResult scan = await WorkspaceScanner.ScanAsync(opened, token);
                string? warning = null;
                try
                {
                    if (recovery is not null)
                        await recovery.RememberAsync(scan.Projects, token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warning = "Git kurtarma kaydı güncellenemedi.";
                }
                return (opened, scan, warning);
            }, token);
            token.ThrowIfCancellationRequested();
            if (background && (_busy || !ReferenceEquals(_workspace, requestedWorkspace)))
                return;
            if (background && _explorerEdit is not null)
            {
                ScheduleWorkspaceRefresh();
                return;
            }

            string selectedNav = SelectedNavigationKey;
            string? selectedRelative = (ProjectGrid.SelectedItem as ProjectEntry)?.RelativePath;
            int topRow = ProjectTableView.TopRowIndex;
            bool sameWorkspace = _workspace?.Root.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase) == true;
            if (!sameWorkspace && _gitView?.IsBusy == true)
            {
                StatusText.Text = "Çalışma alanını değiştirmeden önce Git işleminin bitmesini bekleyin.";
                return;
            }
            if (!sameWorkspace && _workspace is not null)
            {
                _dashboardScan?.Cancel();
                CaptureWorkspaceView();
                ResetGitPane();
                CloseProjectDrawer();
            }
            bool filtersChanged = !(_workspace?.Config.SavedFilters ?? []).SequenceEqual(workspace.Config.SavedFilters ?? []);
            _workspace = workspace;
            WorkspaceSession saved = _session.For(workspace.Root);
            var projects = result.Projects.Select(project => project with { SavedPreference = workspace.PreferenceFor(project.RelativePath) }).ToArray();
            bool projectsChanged = !sameWorkspace || _projects.Count != projects.Length ||
                !_projects.Zip(projects, (previous, current) => previous.HasSameSnapshot(current)).All(equal => equal);
            bool foldersChanged = !sameWorkspace || !_folders.SequenceEqual(result.Folders);
            if (projectsChanged)
                _projects = projects;
            if (foldersChanged)
                _folders = result.Folders;
            WorkspacePath.Text = workspace.Root;
            Title = $"DevWorkspace · {workspace.Config.Name}";
            if (!sameWorkspace)
            {
                _explorerEdit = null;
                _expandedProjectGroups.Clear();
                _expandedProjectGroups.UnionWith(saved.View?.ExpandedProjectGroups ?? []);
                _expandedFolders.Clear();
                _expandedFolders.UnionWith(saved.ExpandedFolders ?? []);
                SearchBox.Text = saved.View?.Search ?? "";
            }
            if (projectsChanged || foldersChanged || filtersChanged)
            {
                string navigation = saved.View is { GitTab: true, Navigation: "all" }
                    ? GitRepositoriesKey : saved.View?.Navigation ?? "all";
                BuildNavigation(sameWorkspace ? selectedNav : navigation);
                string? previousRelative = sameWorkspace ? selectedRelative : saved.SelectedProject;
                if (_visibleProjects.FirstOrDefault(p => p.RelativePath.Equals(previousRelative, StringComparison.OrdinalIgnoreCase)) is { } previous)
                {
                    if (sameWorkspace)
                        ProjectGrid.SelectedItem = previous;
                    else
                        FocusProject(previous);
                }
                if (sameWorkspace)
                    ProjectTableView.TopRowIndex = topRow;
            }
            else
            {
                RefreshProjectDrawer();
            }
            if (!sameWorkspace)
            {
                _session.Remember(workspace.Root, (ProjectGrid.SelectedItem as ProjectEntry)?.RelativePath);
                _session.Save();
            }

            int changed = CountRepositories(p => p.Git.State == GitState.Changed);
            int warnings = CountRepositories(HasGitWarning);
            var details = new List<string> { $"{result.Repositories:N0} Git deposu", $"{changed:N0} değişiklikli" };
            if (warnings > 0)
                details.Add($"{warnings:N0} Git uyarısı");
            if (result.SkippedDirectories > 0)
                details.Add($"{result.SkippedDirectories:N0} klasör atlandı");
            if (recoveryWarning is not null)
                details.Add(recoveryWarning);
            StatusText.Text = string.Join("   ·   ", details);
            StartWorkspaceWatch(workspace);
            if (!sameWorkspace)
                await RestoreWorkspacePanelsAsync(saved.View);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (JsonException)
        {
            if (!background)
                StatusText.Text = $"{Workspace.ConfigFileName} okunamadı. JSON biçimini kontrol edin.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!background)
                StatusText.Text = ex.Message;
        }
        finally
        {
            if (background)
                _backgroundWorkspaceScan = false;
            else if (!_closing.IsCancellationRequested)
                SetBusy(false);
        }
    }

    private int CountRepositories(Func<ProjectEntry, bool> filter) =>
        _projects.Where(filter).Select(p => p.Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static bool HasGitWarning(ProjectEntry project) => project.Git.State is GitState.Unavailable or GitState.Ownership;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OpenWorkspaceMenu.IsEnabled = !busy;
        RefreshMenu.IsEnabled = LinkSolutionMenu.IsEnabled = LinkFolderMenu.IsEnabled = GitNavigationMenu.IsEnabled = !busy && _workspace is not null;
        ExportWorkspaceMenu.IsEnabled = !busy && _workspace is not null;
        ImportWorkspaceMenu.IsEnabled = !busy;
        QuickOpenMenu.IsEnabled = !busy && _projects.Count > 0;
        GitRecoveryMenu.IsEnabled = GitRecoveryFolderMenu.IsEnabled = !busy;
        EmptyOpenButton.IsEnabled = !busy;
        ApplyFilter();
        UpdateGroupSelection();
    }

    private void FilterChanged(object sender, EditValueChangedEventArgs e)
    {
        if (!_busy && SelectedNavigationKey == DashboardKey && !string.IsNullOrWhiteSpace(SearchBox?.Text) &&
            _navigationNodes.TryGetValue("all", out var all))
            Navigation.SelectedItem = all;
        ApplyFilter();
    }
    private void ClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void ApplyFilter()
    {
        if (ProjectGrid is null || SearchBox is null || FilterCount is null || EmptyPanel is null)
            return;

        string query = SearchBox.Text?.Trim() ?? "";
        ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;

        var navigation = SelectedNavigation;
        string key = CurrentProjectFilter?.Navigation ?? navigation?.Key ?? "all";
        UpdateLastSolutionButton();
        GroupVersionsToggle.Visibility = key == "recent" ? Visibility.Collapsed : Visibility.Visible;

        string[] recent = CurrentRecent();
        var recentSet = recent.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = _projects.Where(project => MatchesNavigation(project, key, recentSet) && MatchesSearch(project, query));
        if (key == "recent")
        {
            var ranks = recent.Select((path, index) => (path, index))
                .DistinctBy(item => item.path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(item => item.path, item => item.index, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.OrderBy(project => ranks.GetValueOrDefault(project.RelativePath, int.MaxValue));
        }
        ApplyProjectRows(filtered.ToArray(), GroupVersionsToggle?.IsChecked == true && key != "recent");
        int count = _visibleProjects.Length;

        ViewHeading.Text = navigation?.Label ?? "Tüm projeler";
        ViewCaption.Text = navigation?.Hint ?? "Çalışma alanındaki çözümler ve depolar";
        FilterCount.Text = $"{count:N0} proje";
        bool showScanSummary = _projects.Count > 0 && SelectedNavigationKey == "all" && query.Length == 0;
        ScanSummaryPanel.Visibility = showScanSummary ? Visibility.Visible : Visibility.Collapsed;
        if (showScanSummary)
        {
            int changed = CountRepositories(project => project.Git.State == GitState.Changed);
            int warnings = CountRepositories(HasGitWarning);
            string gitSummary = changed > 0 ? $"{changed:N0} depoda değişiklik var" :
                warnings > 0 ? $"{warnings:N0} Git uyarısı" :
                _projects.Any(project => project.Repository is not null) ? "Yerel değişiklik yok" : "Git deposu bulunamadı";
            ScanSummaryText.Text = $"{_projects.Count:N0} proje bulundu · {gitSummary}";
            ScanSummaryChanges.Visibility = changed > 0 ? Visibility.Visible : Visibility.Collapsed;
            ScanSummaryChanges.IsEnabled = !_busy;
        }
        EmptyPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyOpenButton.Visibility = !_busy && _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyOpenButton.Content = _workspace is null ? "Proje klasörlerini tara" : "Başka klasör seç";
        EmptyTitle.Text = _busy ? "Çalışma alanı taranıyor" :
            _workspace is null ? "Projelerinizi bulun" : "Proje bulunamadı";
        EmptyHint.Text = _busy ? "Projeler ve Git durumları okunuyor." :
            _workspace is null ? "Bir proje klasörü veya projelerinizi içeren üst klasörü seçin. DevWorkspace projeleri ve Git durumlarını listeler." :
            _projects.Count == 0 ? "Bu klasörde desteklenen bir proje veya Git deposu bulunamadı. Başka bir klasör seçin ya da bir proje klasörünü buraya bırakın." :
            "Arama veya sol taraftaki seçimi değiştirin.";
        UpdateCentralView();
    }

    private static bool MatchesSearch(ProjectEntry project, string query) =>
        query.Length == 0 || project.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        project.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (project.Preference.Group ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
        project.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        project.Branch.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesNavigation(ProjectEntry project, string key, HashSet<string> recent)
    {
        if (key.StartsWith("group:", StringComparison.Ordinal))
            return string.Equals(project.Preference.Group, key["group:".Length..], StringComparison.OrdinalIgnoreCase);
        if (key.StartsWith("folder:", StringComparison.Ordinal))
        {
            string prefix = key["folder:".Length..];
            return project.RelativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                project.RelativePath.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        return key switch
        {
            GitRepositoriesKey => project.Repository is not null,
            "changed" => project.Git.State == GitState.Changed,
            "warning" => HasGitWarning(project),
            "favorites" => project.Preference.Favorite,
            "recent" => recent.Contains(project.RelativePath),
            "linked" => project.LinkPath is not null,
            _ => true
        };
    }

    private void ProjectSelectionChanged(object sender, GridSelectionChangedEventArgs e)
    {
        UpdateCompareSelection();
        UpdateGroupSelection();
        if (_gitView is not null && !GitPane.IsClosed)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, CloseGitPaneForOtherProject);
    }

    private void ExplorerClick(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is ProjectEntry project && OpenPath(project.Folder))
            RememberOpened(project);
    }

    private void OpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is ProjectEntry project)
            OpenProject(project);
    }

    private void CopyPathClick(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is ProjectEntry project)
            CopyProjectPath(project);
    }

    private void CopyProjectPath(ProjectEntry project)
    {
        try
        {
            Clipboard.SetText(project.Folder);
            StatusText.Text = "Proje yolu panoya kopyalandı.";
        }
        catch (ExternalException)
        {
            StatusText.Text = "Panoya erişilemedi. Tekrar deneyin.";
        }
    }

    private bool OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            StatusText.Text = ex.Message;
            return false;
        }
    }
}
