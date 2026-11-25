using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DevWorkspace;

public partial class MainWindow
{
    private ContextMenu? _projectContextMenu;
    private bool _initializingGit;

    private void PreserveProjectContextSelection(object sender, MouseButtonEventArgs e)
    {
        if (ProjectGrid.SelectedItems is not { Count: > 1 } selected || e.OriginalSource is not DependencyObject source)
            return;

        var hit = ProjectTableView.CalcHitInfo(source);
        if (hit.InRowCell && ProjectGrid.GetRow(hit.RowHandle) is ProjectEntry project && selected.Contains(project))
            e.Handled = true;
    }

    private void ProjectRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        var hit = ProjectTableView.CalcHitInfo(source);
        if (!hit.InRowCell || ProjectGrid.GetRow(hit.RowHandle) is not ProjectEntry project)
            return;

        e.Handled = true;
        ShowProjectRowMenu(project, keyboard: false);
    }

    private void ProjectGridMenuKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((key == Key.Apps || key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) &&
            ProjectGrid.SelectedItem is ProjectEntry project)
        {
            e.Handled = true;
            ShowProjectRowMenu(project, keyboard: true);
        }
        else
            OpenDrawerAfterKeyboardNavigation(key);
    }

    private void ShowProjectRowMenu(ProjectEntry project, bool keyboard)
    {
        if (_workspace is not Workspace workspace || _busy)
            return;

        var menu = CreateProjectRowMenu(workspace, project, SelectedProjects());
        menu.PlacementTarget = ProjectGrid;
        menu.Placement = keyboard ? PlacementMode.Center : PlacementMode.MousePoint;
        _projectContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_projectContextMenu, menu)) _projectContextMenu = null; };
        menu.IsOpen = true;
    }

    private ContextMenu CreateProjectRowMenu(Workspace workspace, ProjectEntry project, ProjectEntry[] pair)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = project.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        PopulateProjectMenu(menu, workspace, project, pair);
        return menu;
    }

    private void ProjectMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource))
            return;

        ProjectMenu.Items.Clear();
        if (!_busy && _workspace is { } workspace && ProjectGrid.SelectedItem is ProjectEntry project)
            PopulateProjectMenu(ProjectMenu, workspace, project, SelectedProjects());
        else
            ProjectMenu.Items.Add(new MenuItem { Header = Loc.Text("L8B7727A043AF"), IsEnabled = false });
    }

    private void PopulateProjectMenu(ItemsControl menu, Workspace workspace, ProjectEntry project, ProjectEntry[] pair)
    {
        if (!project.Preference.Archived && pair.Length > 1 && pair.Contains(project))
        {
            var group = new MenuItem
            {
                Header = Loc.Format("LC2CB7484AE81", pair.Length),
                Icon = MenuGlyphExtension.Create("\uE8FD"),
                IsEnabled = !_savingPreference
            };
            group.Click += (_, _) =>
            {
                if (_workspace?.Root == workspace.Root)
                    OpenBulkGroupEditor(pair);
            };
            menu.Items.Add(group);
            menu.Items.Add(new Separator());
        }

        void Add(string label, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, Icon = MenuGlyphExtension.Create(glyph), IsEnabled = enabled };
            item.Click += (_, _) =>
            {
                if (_workspace?.Root != workspace.Root)
                    return;

                ProjectEntry? current = _visibleProjects.FirstOrDefault(entry =>
                    entry.RelativePath.Equals(project.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (current is null)
                    return;

                if (!ReferenceEquals(ProjectGrid.SelectedItem, current))
                    ProjectGrid.UnselectAll();
                FocusProject(current);
                action();
            };
            menu.Items.Add(item);
        }

        if (project.OwnerRelativePath is { } ownerPath && project.LinkPath is { } componentLink)
        {
            Add(Loc.Text("L0BC9CF040BE2"), "\uE72B", () =>
            {
                if (FindComponentOwner(ownerPath) is { } owner)
                    NavigateToComponentOwner(owner);
            }, !_busy && FindComponentOwner(ownerPath) is not null);
            Add(Loc.Text("L150C17378129"), "\uE711",
                async () => await SetComponentRowAsync(workspace, componentLink, ownerPath, hide: true, returnToOwner: true), !_busy);
            menu.Items.Add(new Separator());
        }

        bool archived = project.Preference.Archived;
        var components = _projectComponents.Where(component =>
            component.OwnerFolder.Equals(project.Folder, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!archived && components.Length > 0)
        {
            string[] opened = components.Where(IsComponentLinked).Select(component => component.Folder)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] closed = components.Where(component => !IsComponentLinked(component)).Select(component => component.Folder)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Add(Loc.Text("L3DD95D714AF6"), "\uE710",
                async () => await SetComponentRowsAsync(workspace, closed, project.RelativePath, hide: false, returnToOwner: true),
                !_busy && closed.Length > 0);
            Add(Loc.Text("L79792D4AF16F"), "\uE711",
                async () => await SetComponentRowsAsync(workspace, opened, project.RelativePath, hide: true, returnToOwner: true),
                !_busy && opened.Length > 0);
            menu.Items.Add(new Separator());
        }
        Add(archived ? Loc.Text("LFCD7700E64BF") : Loc.Text("LB1048C007FD9"), "\uE7B8",
            () => ChangeProjectArchive(project, !archived), CanChangeProjectArchive);
        if (archived)
        {
            Add(Loc.Text("LD6F2F6CAD078"), "\uE838", () => ExplorerClick(this, new()), Directory.Exists(project.Folder));
            Add(Loc.Text("L72C32847EB3E"), "\uE8C8", () => CopyPathClick(this, new()));
            return;
        }
        menu.Items.Add(new Separator());

        bool folderExists = Directory.Exists(project.Folder);
        Add(project.Kind == "Visual Studio" ? Loc.Text("L686C9A600047") : Loc.Text("L439CC142CAA2"), "\uE8A7",
            () => OpenProjectClick(this, new()), folderExists);
        if (VisualStudioInstallations.Supports(project.EntryFile))
            Add(Loc.Text("VisualStudioChange"), "\uE713", () => ChangeProjectVisualStudio(project), folderExists);
        Add(Loc.Text("LD6F2F6CAD078"), "\uE838", () => ExplorerClick(this, new()), folderExists);
        Add(Loc.Text("L72C32847EB3E"), "\uE8C8", () => CopyPathClick(this, new()));
        var releaseProjects = ProjectTools.FindReleaseProjects(project.EntryFile);
        var releaseMenu = new MenuItem { Header = ReleaseText.T("Title"), Icon = MenuGlyphExtension.Create("\uE898"),
            IsEnabled = folderExists && releaseProjects.Count > 0,
            ToolTip = releaseProjects.Count == 0 ? ReleaseText.T("UnsupportedContext") : null };
        void OpenRelease(string file)
        {
            if (_workspace?.Root == workspace.Root) OpenProjectReleaseCenter(file);
        }
        if (releaseProjects.Count == 1)
            releaseMenu.Click += (_, _) => OpenRelease(releaseProjects[0]);
        else
            foreach (string file in releaseProjects)
            {
                var target = new MenuItem { Header = Path.GetRelativePath(project.Folder, file).Replace("_", "__") };
                target.Click += (_, e) => { e.Handled = true; OpenRelease(file); };
                releaseMenu.Items.Add(target);
            }
        menu.Items.Add(releaseMenu);
        bool favorite = project.Preference.Favorite;
        Add(favorite ? Loc.Text("LC81AC7A60617") : Loc.Text("LC1F494543AF2"), favorite ? "\uE735" : "\uE734",
            () => FavoriteClick(this, new()));
        menu.Items.Add(new Separator());
        if (project.Repository is not null)
        {
            Add(Loc.Text("UncommittedChanges"), "\uE8A5", () => OpenProjectDiff(project), folderExists);
            Add(Loc.Text("L8C9C5A963356"), MenuGlyphExtension.GitHistory, () => new GitHistoryWindow(project) { Owner = this }.ShowDialog());
            Add(Loc.Text("LD11171088FD5"), MenuGlyphExtension.GitOperations, () => OpenGitWorkbench(project));
            Add(Loc.Text("L405B8B84BC36"), "\uE8F1", () =>
            {
                if (EnsureTool(DevelopmentTool.Git))
                    new GitWorktreeWindow(project.Repository, path => OpenPath(path)) { Owner = this }.ShowDialog();
            });
            if (HasGitWarning(project) || project.Git.State == GitState.Changed)
                Add("Git bulgusunu takip et…", "\uE7BA", () => TrackGitFinding(project));
        }
        else
            Add(Loc.Text("L55506B22BDCE"), "\uE710", () => InitializeProjectGitAsync(project), folderExists && !_initializingGit);
        Add(Loc.Text("L8043F450A004"), "\uE8A5", () => OpenProjectDetail(ProjectDetailView.Documents), folderExists);
        Add(Loc.Text("L407D86327F61"), "\uE946", () => OpenProjectDetail(ProjectDetailView.Purpose));

        menu.Items.Add(new Separator());
        Add(Loc.Text("L7CB1799388E1"), "\uE713", () => ProjectCommandsClick(this, new()), folderExists);
        Add(Loc.Text("LA28B52C659C7"), "\uE9D9", () => ProjectHealthClick(this, new()));
        Add(Loc.Text("LDF24BC28A25C"), "\uE71B", () =>
            new ProjectInspectionWindow(Loc.Format("L9A357CE10D90", project.Name),
                token => Task.FromResult(ProjectInspection.Relationships(project, _projects, token))) { Owner = this }.ShowDialog(),
            project.EntryFile is { } entry && ProjectTools.IsDotNetEntry(entry));
        Add(Loc.Text("LC2580239A435"), "\uE713", () => OpenProjectEnvironment(project), folderExists);
        Add(Loc.Text("L6031B86875C3"), "\uE8EC", () => OpenProjectDetail(ProjectDetailView.Organization));
        if (pair.Length == 2 && pair.Contains(project) && !pair[0].Folder.Equals(pair[1].Folder, StringComparison.OrdinalIgnoreCase))
            Add(Loc.Text("L3EB9B19DECBE"), "\uE8AB", () => new ComparisonWindow(workspace, pair[0], pair[1]) { Owner = this }.ShowDialog());
        else
            Add(Loc.Text("L28418C937C30"), "\uE8AB", () => CompareProjectClick(this, new()));
        Add(Loc.Text("L29290972B888"), "\uE8DE", () => PreviewMoveClick(this, new()), folderExists && project.LinkPath is null && !project.IsWorkspaceRoot);
        if (project.LinkPath is not null)
            Add(Loc.Text("L11F80F8CF5B1"), "\uE738", () => UnlinkProjectClick(this, new()));
    }

    private void OpenProjectEnvironment(ProjectEntry project)
    {
        if (_workspace is { } workspace)
            new ProjectInspectionWindow(Loc.Format("L0CA64D8B19A5", project.Name),
                token => ProjectInspection.EnvironmentAsync(workspace, project, token)) { Owner = this }.ShowDialog();
    }

    private async void OpenProjectDetail(ProjectDetailView view, ProjectEntry? project = null)
    {
        project ??= ProjectGrid.SelectedItem as ProjectEntry;
        if (_workspace is not Workspace workspace || project is null)
            return;

        var dialog = new ProjectDetailWindow(workspace, project, view, _projects) { Owner = this };
        bool? accepted = dialog.ShowDialog();
        if (dialog.UpdatedWorkspace is Workspace updated && _workspace?.Root == updated.Root)
        {
            _workspace = updated;
            RefreshProjectDrawer();
        }
        if (view == ProjectDetailView.Organization && accepted == true)
        {
            string group = dialog.Group;
            string label = dialog.Label;
            await SavePreferenceAsync(current => current with { Group = group, Label = label });
        }
    }

    private async void InitializeProjectGitAsync(ProjectEntry project)
    {
        if (_initializingGit || project.Repository is not null || !Directory.Exists(project.Folder))
            return;
        if (!EnsureTool(DevelopmentTool.Git))
            return;

        string? workspace = _workspace?.Root;
        string ignoreInfo = Loc.Text("IgnoreInfo");
        if (MessageBox.Show(this, Loc.Format("LF660D25C7FC2", project.Folder, ignoreInfo),
            Loc.Text("LF39C23D70767"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _initializingGit = true;
        try
        {
            await GitProjectOperations.InitializeAsync(project.Folder, project.Kind, _closing.Token, project.EntryFile);
            if (_workspace?.Root == workspace)
                await RefreshAsync();
            StatusText.Text = Loc.Text("LC0EA8EBC6685");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!_closing.IsCancellationRequested)
                StatusText.Text = ex.Message;
        }
        finally { _initializingGit = false; }
    }
}
