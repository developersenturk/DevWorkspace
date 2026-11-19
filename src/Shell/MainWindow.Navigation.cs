using System.IO;
using System.Windows;
using DevExpress.Xpf.Grid.TreeList;

namespace DevWorkspace;

public partial class MainWindow
{
    private const string GitRepositoriesKey = "repositories";
    private const string ArchivedProjectsKey = "archived";

    private sealed class NavigationItem(string key, string label, string icon, string hint, int? count)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public string Icon { get; } = icon;
        public string Hint { get; } = hint;
        public int? Count { get; } = count;
        public bool IsSection => Key.StartsWith("section:", StringComparison.Ordinal);
        public string? FolderPath { get; init; }
        public bool IsFolderSection => Key == "section:KLASÖRLER";
        public ExplorerFolderEdit? FolderEdit { get; set; }
        public bool IsEditing => FolderEdit is not null;
        public bool HasFolderActions => !IsEditing && (FolderPath is not null || IsFolderSection);
        public bool Expanded { get; set; }
        public Func<List<NavigationItem>>? LoadChildren { get; init; }
        private List<NavigationItem>? _children;
        public List<NavigationItem> Children => _children ??= LoadChildren?.Invoke() ?? [];
        public bool HasChildren => _children?.Count > 0 || _children is null && LoadChildren is not null;
    }

    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NavigationItem> _navigationNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _folderCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _folderChildren = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkspaceFolder> _folderSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _folderOrder = new(StringComparer.OrdinalIgnoreCase);
    private string _navigationToRestore = "all";
    private NavigationItem? _activeNavigation;
    private NavigationItem? SelectedNavigation => _activeNavigation;
    private string SelectedNavigationKey => SelectedNavigation?.Key ?? "all";
    private SavedProjectFilter? CurrentProjectFilter =>
        _workspace?.Config.SavedFilters?.FirstOrDefault(filter => filter.Key == SelectedNavigationKey);

    private void BuildNavigation(string selectedKey)
    {
        _buildingNavigation = true;
        try
        {
            _navigationNodes.Clear();
            _folderCounts.Clear();
            _folderChildren.Clear();
            _folderSettings.Clear();
            _folderOrder.Clear();
            foreach (WorkspaceFolder folder in _folders)
                _folderSettings[folder.Name] = folder;
            string[] orderedSubfolders = _workspace?.Config.ExplorerSubfolders ?? [];
            for (int index = 0; index < orderedSubfolders.Length; index++)
                _folderOrder.TryAdd(Workspace.ExplorerFolderPath(orderedSubfolders[index]), index);
            var sections = new List<NavigationItem>();
            foreach (ProjectEntry project in _projects.Where(project => project.LinkPath is null && !project.IsWorkspaceRoot))
            {
                for (string? path = project.RelativePath; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                    _folderCounts[path] = _folderCounts.GetValueOrDefault(path) + 1;
            }
            foreach (WorkspaceFolder folder in _folders.Where(folder => folder.Name.Contains(Path.DirectorySeparatorChar) && IsExplorerFolderVisible(folder.Name)))
            {
                for (string? path = folder.Name; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                    _folderCounts.TryAdd(path, 0);
            }
            foreach (string path in _folderCounts.Keys)
            {
                if (Path.GetDirectoryName(path) is not { Length: > 0 } parent)
                    continue;
                if (!_folderChildren.TryGetValue(parent, out var children))
                    _folderChildren.Add(parent, children = []);
                children.Add(path);
            }

            if (selectedKey.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                string? path = selectedKey["folder:".Length..];
                while (!string.IsNullOrEmpty(path) && !_folderCounts.ContainsKey(path) &&
                       !_folders.Any(folder => folder.IsVisible && folder.Name.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    path = Path.GetDirectoryName(path);
                selectedKey = string.IsNullOrEmpty(path) ? "all" : $"folder:{path}";
            }
            _navigationToRestore = selectedKey;

            var overview = Section("ÇALIŞMA ALANI", sections);
            overview.Children.Add(Node(new(DashboardKey, Loc.Text("L699B4A551D76"), "\uE80A", Loc.Text("L0DA747F259DD"), null)));
            overview.Children.Add(Node(new("all", Loc.Text("L1191AFF270DA"), "\uE80A", Loc.Text("L997C1AE39488"), _projects.Count)));
            if (_workspace is not null)
            {
                overview.Children.Add(Node(new(ArchivedProjectsKey, Loc.Text("L7A9E71860BEB"), "\uE7B8",
                    Loc.Text("L7B215054C727"), _archivedProjects.Count)));
                var recent = CurrentRecent().ToHashSet(StringComparer.OrdinalIgnoreCase);
                overview.Children.Add(Node(new("favorites", Loc.Text("L13E9F8C058C9"), "\uE734", Loc.Text("LDE2B87364E10"),
                    _projects.Count(project => project.Preference.Favorite))));
                overview.Children.Add(Node(new("recent", Loc.Text("LA46393ADBAA2"), "\uE81C", Loc.Text("LA6F0F60657C9"),
                    _projects.Count(project => recent.Contains(project.RelativePath)))));
            }

            WorkspaceFolder[] roots = _folders.Where(folder => folder.IsVisible && !folder.Name.Contains(Path.DirectorySeparatorChar)).ToArray();
            if (_workspace is not null)
            {
                var folders = Section("KLASÖRLER", sections);
                foreach (WorkspaceFolder folder in roots)
                    folders.Children.Add(FolderNode(folder.Name, folder.Label));
                if (_explorerEdit is { Rename: false, Path: null })
                    folders.Children.Add(NewFolderRow());
            }
            int linkedCount = _projects.Count(project => project.LinkPath is not null);
            if (linkedCount > 0)
                overview.Children.Add(Node(new("linked", Loc.Text("L68F3F11503E7"), "\uE71B", Loc.Text("L2B707D2D2055"), linkedCount)));

            var git = Section("GİT", sections);
            git.Children.Add(Node(new(GitRepositoriesKey, Loc.Text("LBBF8DEC3700C"), "\uE8F1", Loc.Text("L25CDA9C7CD81"),
                _projects.Count(project => project.Repository is not null))));
            git.Children.Add(Node(new("changed", Loc.Text("L015C1B0968C6"), "\uE70F", Loc.Text("L7AC022BEDCCB"),
                _projects.Count(project => project.Git.State == GitState.Changed))));
            git.Children.Add(Node(new("warning", Loc.Text("L087EEE8F2B23"), "\uE7BA", Loc.Text("L40C02B8FDA54"), _projects.Count(HasGitWarning))));

            var groups = _projects.Where(project => !string.IsNullOrWhiteSpace(project.Preference.Group))
                .GroupBy(project => project.Preference.Group, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (groups.Length > 0)
            {
                var section = Section("PROJE GRUPLARI", sections);
                foreach (var group in groups)
                    section.Children.Add(Node(new($"group:{group.Key}", group.Key, "\uE8EC", Loc.Text("LA131CD96CAE8"), group.Count())));
            }

            if (_workspace?.Config.SavedFilters is { Length: > 0 } filters)
            {
                var section = Section("KAYITLI FİLTRELER", sections);
                var recent = CurrentRecent().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var filter in filters.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    string scope = _navigationNodes.GetValueOrDefault(filter.Navigation)?.Label ?? filter.Navigation;
                    string hint = filter.Query.Length == 0 ? scope : $"{scope} · {filter.Query}";
                    section.Children.Add(Node(new(filter.Key, filter.Name, "\uE71C", hint,
                        (filter.Navigation == ArchivedProjectsKey ? _archivedProjects : _projects)
                            .Count(project => MatchesNavigation(project, filter.Navigation, recent) && MatchesSearch(project, filter.Query)))));
                }
            }

            Navigation.ItemsSource = sections;
            _activeNavigation = _navigationNodes.GetValueOrDefault(selectedKey) ?? _navigationNodes["all"];
            Navigation.SelectedItem = _explorerEdit is { } edit
                ? _navigationNodes.GetValueOrDefault(edit.Key) ?? _activeNavigation
                : _activeNavigation;
        }
        finally
        {
            _buildingNavigation = false;
            _navigationToRestore = "all";
        }
        ApplyFilter();
    }

    private NavigationItem Section(string title, List<NavigationItem> sections)
    {
        var item = new NavigationItem($"section:{title}", Loc.Label(title), "", Loc.Label(title), null) { Expanded = true };
        sections.Add(item);
        return item;
    }

    private NavigationItem Node(NavigationItem navigation)
    {
        if (_explorerEdit is { } edit && navigation.Key.Equals(edit.Key, StringComparison.OrdinalIgnoreCase))
            navigation.FolderEdit = edit;
        _navigationNodes.Add(navigation.Key, navigation);
        return navigation;
    }

    private NavigationItem NewFolderRow() => Node(new(NewFolderKey, Loc.Text("L8D62DD79B534"), "\uE8B7", Loc.Text("LD6D340899CDA"), null));

    private NavigationItem FolderNode(string relativePath, string label)
    {
        bool editingParent = _explorerEdit is { Rename: false, Path: { } parent } &&
            parent.Equals(relativePath, StringComparison.OrdinalIgnoreCase);
        string? editPath = _explorerEdit?.Path;
        bool expanded = _expandedFolders.Contains(relativePath) ||
            _navigationToRestore.StartsWith($"folder:{relativePath}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            editingParent || editPath?.StartsWith(relativePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;
        List<string>? children = _folderChildren.GetValueOrDefault(relativePath);
        var item = Node(new NavigationItem($"folder:{relativePath}", label, "\uE8B7", relativePath, _folderCounts.GetValueOrDefault(relativePath))
        {
            FolderPath = relativePath,
            Expanded = expanded,
            LoadChildren = children is not null || editingParent
                ? () => FolderNodes(children, editingParent)
                : null
        });
        if (expanded)
            _ = item.Children;
        return item;
    }

    private List<NavigationItem> FolderNodes(List<string>? children, bool includeDraft)
    {
        var nodes = OrderedFolderChildren(children ?? [])
            .Select(path => FolderNode(path, _folderSettings.GetValueOrDefault(path)?.Label ?? Path.GetFileName(path))).ToList();
        if (includeDraft)
            nodes.Add(NewFolderRow());
        return nodes;
    }

    private bool IsExplorerFolderVisible(string relativePath)
    {
        for (string? path = relativePath; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if (_folderSettings.GetValueOrDefault(path) is { IsVisible: false })
                return false;
        return true;
    }

    private IEnumerable<string> OrderedFolderChildren(IEnumerable<string> paths) =>
        paths.OrderBy(path => _folderOrder.GetValueOrDefault(path, int.MaxValue))
            .ThenBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase);

    private void FolderExpanded(object sender, TreeViewNodeEventArgs e) => UpdateFolderExpansion(e, true);
    private void FolderCollapsed(object sender, TreeViewNodeEventArgs e) => UpdateFolderExpansion(e, false);

    private void UpdateFolderExpansion(TreeViewNodeEventArgs e, bool expanded)
    {
        if (_buildingNavigation || _workspace is null || e.Node.Content is not NavigationItem { FolderPath: string path })
            return;
        if (expanded)
            _expandedFolders.Add(path);
        else
            _expandedFolders.Remove(path);
        _session.RememberExpandedFolders(_workspace.Root, _expandedFolders);
    }

    private void NavigationChanged(object sender, RoutedEventArgs e)
    {
        if (!_buildingNavigation && Navigation.SelectedItem is NavigationItem { IsSection: false, IsEditing: false } item)
        {
            _activeNavigation = item;
            if (item.Key == DashboardKey)
                GitDiffPanel.Visibility = Visibility.Collapsed;
            if (CurrentProjectFilter is { } filter)
                SearchBox.Text = filter.Query;
            ApplyFilter();
        }
    }
}
