using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DevWorkspace;

public partial class MainWindow
{
    private DispatcherTimer? _workspaceRefresh;
    private readonly List<FileSystemWatcher> _workspaceWatchers = [];
    private string? _watchKey;
    private int _watchGeneration;
    private bool _backgroundWorkspaceScan;

    private void InitializeWorkspaceRefresh()
    {
        if (Application.Current is { } app)
            app.Activated += WorkspaceActivated;

        _workspaceRefresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(850) };
        _workspaceRefresh.Tick += async (_, _) =>
        {
            if (_closing.IsCancellationRequested || _workspace is null)
            {
                _workspaceRefresh.Stop();
                return;
            }

            if (_busy || _backgroundWorkspaceScan || IsMainMenuOpen || _projectContextMenu?.IsOpen == true ||
                _savingPreference || _savingExplorerFolder || _explorerEdit is not null || _explorerContextMenu?.IsOpen == true ||
                OwnedWindows.OfType<Window>().Any(window => window.IsVisible && window is not OperationCenterWindow))
                return;

            _workspaceRefresh.Stop();
            await OpenWorkspaceAsync(_workspace.Root, background: true);
        };
    }

    private void WorkspaceActivated(object? sender, EventArgs e)
    {
        if (_workspace is not null)
            ScheduleWorkspaceRefresh();
    }

    private void ScheduleWorkspaceRefresh()
    {
        if (_closing.IsCancellationRequested || _workspaceRefresh is null)
            return;
        _workspaceRefresh.Stop();
        _workspaceRefresh.Start();
    }

    private void StartWorkspaceWatch(Workspace workspace)
    {
        string[] links = workspace.Config.LinkedProjects ?? [];
        string[] repositories = _projects.Concat(_archivedProjects).Select(project => project.Repository)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string key = workspace.Root + "\n" + string.Join('\n', workspace.Config.ScanExclusions) + "\n" +
            string.Join('\n', links) + "\n" + string.Join('\n', repositories);
        if (_watchKey == key)
            return;

        StopWorkspaceRefresh();
        _watchKey = key;
        int generation = _watchGeneration;
        void QueueRefresh() => Dispatcher.BeginInvoke(() =>
        {
            if (generation == _watchGeneration)
                ScheduleWorkspaceRefresh();
        });

        var exclusions = workspace.Config.ScanExclusions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        exclusions.UnionWith(ProjectDetection.GeneratedFolders);
        exclusions.Add(".git");
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { workspace.Root };
        foreach (string link in links)
        {
            if (string.IsNullOrWhiteSpace(link) || !Path.IsPathFullyQualified(link))
                continue;
            string? path = Directory.Exists(link) ? link : Path.GetDirectoryName(link);
            if (path is not null && Directory.Exists(path) &&
                !roots.Any(root => IsInside(path, root)))
                roots.Add(path);
        }

        foreach (string root in roots)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName
                };
                bool IsRelevant(string path)
                {
                    string relative = Path.GetRelativePath(root, path);
                    return !Path.GetFileName(path).Equals(Workspace.ConfigFileName, StringComparison.OrdinalIgnoreCase) &&
                        !Path.GetFileName(path).StartsWith(".devworkspace-", StringComparison.OrdinalIgnoreCase) &&
                        !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(exclusions.Contains) &&
                        (root.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase) ||
                         links.Any(link => !string.IsNullOrWhiteSpace(link) && (IsInside(path, link) || IsInside(link, path))));
                }
                void Changed(object sender, FileSystemEventArgs e)
                {
                    if (IsRelevant(e.FullPath))
                        QueueRefresh();
                }
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += (_, e) => { if (IsRelevant(e.FullPath) || IsRelevant(e.OldFullPath)) QueueRefresh(); };
                watcher.Error += (_, _) => QueueRefresh();
                watcher.EnableRaisingEvents = true;
                _workspaceWatchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
            }
        }

        foreach (string directory in repositories.SelectMany(GitMetadataDirectories).Distinct(StringComparer.OrdinalIgnoreCase))
            WatchGitMetadata(directory, QueueRefresh);
    }

    private static IReadOnlyList<string> GitMetadataDirectories(string repository)
    {
        try
        {
            string git = Path.Combine(repository, ".git");
            if (File.Exists(git))
            {
                string pointer = File.ReadAllText(git).Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    return [];
                git = Path.GetFullPath(pointer["gitdir:".Length..].Trim(), repository);
            }
            if (!Directory.Exists(git))
                return [];

            string common = Path.Combine(git, "commondir");
            return File.Exists(common)
                ? [git, Path.GetFullPath(File.ReadAllText(common).Trim(), git)]
                : [git];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private void WatchGitMetadata(string directory, Action refresh)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };
            bool IsStatusFile(string path)
            {
                string relative = Path.GetRelativePath(directory, path);
                return !relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
                    (relative is "HEAD" or "index" or "packed-refs" or "config" or "config.worktree" ||
                     IsInside(path, Path.Combine(directory, "refs")));
            }
            void Changed(object sender, FileSystemEventArgs e)
            {
                if (IsStatusFile(e.FullPath))
                    refresh();
            }
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += (_, e) => { if (IsStatusFile(e.FullPath) || IsStatusFile(e.OldFullPath)) refresh(); };
            watcher.Error += (_, _) => refresh();
            watcher.EnableRaisingEvents = true;
            _workspaceWatchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            watcher?.Dispose();
        }
    }

    private void StopWorkspaceRefresh()
    {
        _workspaceRefresh?.Stop();
        _watchGeneration++;
        foreach (FileSystemWatcher watcher in _workspaceWatchers)
            watcher.Dispose();
        _workspaceWatchers.Clear();
        _watchKey = null;
    }

    private static bool IsInside(string path, string parent)
    {
        string root = Path.TrimEndingDirectorySeparator(parent);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void FileMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource))
            return;

        string[] recent = _session.Workspaces.OrderByDescending(pair => pair.Value?.LastUsedUtc)
            .Select(pair => pair.Key).Where(path => !path.Equals(_workspace?.Root, StringComparison.OrdinalIgnoreCase) &&
                Path.IsPathFullyQualified(path) && Directory.Exists(path)).Take(6).ToArray();
        RecentWorkspacesMenu.Items.Clear();
        RecentWorkspacesMenu.IsEnabled = !_busy && recent.Length > 0;
        foreach (string path in recent)
        {
            var item = new MenuItem { Header = new TextBlock { Text = path }, Tag = path, Icon = MenuGlyphExtension.Create("\uE8B7") };
            item.Click += SwitchWorkspaceClick;
            RecentWorkspacesMenu.Items.Add(item);
        }
    }

    private async void SwitchWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path } && !_busy)
            await OpenWorkspaceAsync(path);
    }

    private void ProjectDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        e.Effects = _workspace is null || _busy ? DragDropEffects.None :
            e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void ProjectDrop(object sender, DragEventArgs e)
    {
        if (_workspace is null || _busy || e.Data.GetData(DataFormats.FileDrop) is not string[] dropped)
            return;

        e.Handled = true;
        await AttachProjectsAsync(dropped);
    }

    private async void LinkSolutionClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _busy)
        {
            StatusText.Text = Loc.Text("LDE63CBFC4B33");
            return;
        }
        var picker = new OpenFileDialog
        {
            Title = Loc.Text("L5D005E4F1E10"), Multiselect = true,
            Filter = Loc.Text("LCD0DCFC442B3")
        };
        if (picker.ShowDialog(this) == true)
            await AttachProjectsAsync(picker.FileNames);
    }

    private async void LinkFolderClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _busy)
        {
            StatusText.Text = Loc.Text("LDE63CBFC4B33");
            return;
        }
        var picker = new OpenFolderDialog { Title = Loc.Text("L50B45ECDBA99") };
        if (picker.ShowDialog(this) == true)
            await AttachProjectsAsync([picker.FolderName]);
    }

    private async Task<ProjectEntry?> AttachProjectsAsync(IEnumerable<string> dropped, string? clonedRepository = null)
    {
        if (_workspace is not Workspace workspace || _busy)
            return null;

        try
        {
            string[] paths = dropped.Select(Workspace.NormalizeLink)
                .Where(path => !path.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (paths.Length == 0)
                return null;

            string[] newLinks = paths.Where(path => !_projects.Concat(_archivedProjects).Any(project => project.LinkPath is null &&
                (path.Equals(project.Folder, StringComparison.OrdinalIgnoreCase) || path.Equals(project.EntryFile, StringComparison.OrdinalIgnoreCase))))
                .ToArray();
            if (newLinks.Length > 0)
                _workspace = workspace.AddLinks(newLinks);

            await RefreshAsync();
            var archived = _archivedProjects.FirstOrDefault(project => paths.Any(path =>
                path.Equals(project.LinkPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(project.Folder, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(project.EntryFile, StringComparison.OrdinalIgnoreCase)));
            var linked = _projects.FirstOrDefault(project => paths.Any(path => path.Equals(project.LinkPath, StringComparison.OrdinalIgnoreCase)));
            var existing = linked ?? _projects.FirstOrDefault(project => paths.Any(path =>
                path.Equals(project.Folder, StringComparison.OrdinalIgnoreCase) || path.Equals(project.EntryFile, StringComparison.OrdinalIgnoreCase))) ??
                _projects.FirstOrDefault(project => clonedRepository?.Equals(project.Repository, StringComparison.OrdinalIgnoreCase) == true);
            SearchBox.Text = "";
            BuildNavigation(linked is not null ? "linked" : existing is null && archived is not null ? ArchivedProjectsKey : "all");
            if (existing is not null)
                FocusProject(existing);
            else if (archived is not null)
                FocusProject(archived);
            StatusText.Text = linked is not null ? Loc.Text("LDA04A85424CB") :
                existing is null && archived is not null ? Loc.Text("LA2B45BAC0E1B") : Loc.Text("L416A9740F156");
            return existing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            StatusText.Text = ex.Message;
            return null;
        }
    }

    private async void UnlinkProjectClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is not Workspace workspace || _busy || ProjectGrid.SelectedItem is not ProjectEntry { LinkPath: string path })
            return;

        try
        {
            _workspace = workspace.RemoveLink(path);
            await RefreshAsync();
            StatusText.Text = Loc.Text("L3AB7EFC0BB94");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText.Text = ex.Message;
        }
    }
}

