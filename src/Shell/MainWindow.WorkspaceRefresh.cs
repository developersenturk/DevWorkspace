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
                _savingExplorerFolder || _explorerEdit is not null || _explorerContextMenu?.IsOpen == true ||
                OwnedWindows.OfType<Window>().Any(window => window.IsVisible))
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
        string key = workspace.Root + "\n" + string.Join('\n', workspace.Config.ScanExclusions) + "\n" + string.Join('\n', links);
        if (_watchKey == key)
            return;

        StopWorkspaceRefresh();
        _watchKey = key;
        int generation = _watchGeneration;
        var exclusions = workspace.Config.ScanExclusions.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                void QueueRefresh()
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (generation == _watchGeneration)
                            ScheduleWorkspaceRefresh();
                    });
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
                Path.IsPathFullyQualified(path) && File.Exists(Path.Combine(path, Workspace.ConfigFileName))).Take(6).ToArray();
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
            StatusText.Text = "Proje bağlamak için önce bir çalışma alanı açın.";
            return;
        }
        var picker = new OpenFileDialog
        {
            Title = "Bağlanacak çözüm veya projeyi seçin", Multiselect = true,
            Filter = "Çözüm ve proje dosyaları|*.sln;*.slnx;*.csproj;*.vbproj;*.fsproj;*.vcxproj"
        };
        if (picker.ShowDialog(this) == true)
            await AttachProjectsAsync(picker.FileNames);
    }

    private async void LinkFolderClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || _busy)
        {
            StatusText.Text = "Proje bağlamak için önce bir çalışma alanı açın.";
            return;
        }
        var picker = new OpenFolderDialog { Title = "Bağlanacak proje klasörünü seçin" };
        if (picker.ShowDialog(this) == true)
            await AttachProjectsAsync([picker.FolderName]);
    }

    private async Task AttachProjectsAsync(IEnumerable<string> dropped)
    {
        if (_workspace is not Workspace workspace || _busy)
            return;

        try
        {
            string[] paths = dropped.Select(Workspace.NormalizeLink)
                .Where(path => !path.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (paths.Length == 0)
                return;

            string[] newLinks = paths.Where(path => !_projects.Any(project => project.LinkPath is null &&
                (path.Equals(project.Folder, StringComparison.OrdinalIgnoreCase) || path.Equals(project.EntryFile, StringComparison.OrdinalIgnoreCase))))
                .ToArray();
            if (newLinks.Length > 0)
                _workspace = workspace.AddLinks(newLinks);

            await RefreshAsync();
            var linked = _projects.FirstOrDefault(project => paths.Any(path => path.Equals(project.LinkPath, StringComparison.OrdinalIgnoreCase)));
            var existing = linked ?? _projects.FirstOrDefault(project => paths.Any(path =>
                path.Equals(project.Folder, StringComparison.OrdinalIgnoreCase) || path.Equals(project.EntryFile, StringComparison.OrdinalIgnoreCase)));
            SearchBox.Text = "";
            BuildNavigation(linked is not null ? "linked" : "all");
            if (existing is not null && _visibleProjects.Contains(existing))
                ProjectGrid.SelectedItem = existing;
            StatusText.Text = linked is not null ? "Proje kendi konumunda çalışma alanına bağlandı." : "Proje çalışma alanında görünüyor.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            StatusText.Text = ex.Message;
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
            StatusText.Text = "Proje bağlantısı kaldırıldı.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText.Text = ex.Message;
        }
    }
}

