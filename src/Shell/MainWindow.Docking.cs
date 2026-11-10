using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class MainWindow
{
    private GitWorkbenchView? _gitView;

    private void WindowsMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource))
            return;

        ShowProjectInfoMenu.IsEnabled = !_busy && ProjectGrid.SelectedItem is ProjectEntry;
        ShowGitMenu.IsEnabled = !_busy && ProjectGrid.SelectedItem is ProjectEntry { Repository: not null };
        PopulateSavedLayoutsMenu();
    }

    private void ShowExplorerClick(object sender, RoutedEventArgs e)
    {
        if (ExplorerPane.IsAutoHidden)
            WorkbenchDock.DockController.Dock(ExplorerPane);
        WorkbenchDock.Activate(ExplorerPane);
    }

    private void ShowProjectListClick(object sender, RoutedEventArgs e)
    {
        if (SelectedNavigationKey == DashboardKey)
            NavigateFromDashboard("all");
        CloseGitDiff();
        WorkbenchDock.Activate(ProjectsPane);
    }

    private void ShowProjectInfoClick(object sender, RoutedEventArgs e)
    {
        if (!_busy && ProjectGrid.SelectedItem is ProjectEntry project)
            ShowProjectDrawer(project);
    }

    private void ShowGitClick(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is ProjectEntry project)
            OpenGitWorkbench(project);
    }

    private void GitMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource))
            return;

        var project = ProjectGrid.SelectedItem as ProjectEntry;
        SelectedRepositoryMenu.Header = project?.Repository is not null
            ? new TextBlock { Text = $"Seçili depo · {project.Name}" } : "Seçili depo";
        SelectedRepositoryMenu.IsEnabled = !_busy && _gitView?.IsBusy != true && project?.Repository is not null;
        BatchFetchMenu.IsEnabled = !_busy && _gitView?.IsBusy != true && SelectedProjects().Any(item => item.Repository is not null);
    }

    private async void RepositoryCommandClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _gitView?.IsBusy == true || sender is not MenuItem { Tag: string command } ||
            ProjectGrid.SelectedItem is not ProjectEntry { Repository: not null } project)
            return;

        if (command == "History")
        {
            new GitHistoryWindow(project) { Owner = this }.ShowDialog();
            return;
        }
        if (command == "References")
        {
            var window = new GitReferencesWindow(project) { Owner = this };
            window.ShowDialog();
            if (window.RepositoryChanged)
                await RefreshRepositoryViewsAsync();
            return;
        }

        var view = await OpenGitWorkbenchAsync(project);
        if (view is not null && !GitPane.IsClosed && MatchesGitProject(ProjectGrid.SelectedItem as ProjectEntry, project))
            await view.RunRepositoryMenuCommandAsync(command);
    }

    private async void BatchFetchClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _gitView?.IsBusy == true)
            return;

        string[] repositories = SelectedProjects().Select(project => project.Repository).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (repositories.Length == 0)
            return;

        new BatchFetchWindow(repositories) { Owner = this }.ShowDialog();
        await RefreshRepositoryViewsAsync();
    }

    private async Task RefreshRepositoryViewsAsync()
    {
        await RefreshAsync();
        if (_gitView is { } view && !GitPane.IsClosed)
            await view.RefreshAsync();
    }

    private async void OpenGitWorkbench(ProjectEntry project) => await OpenGitWorkbenchAsync(project);

    private async Task<GitWorkbenchView?> OpenGitWorkbenchAsync(ProjectEntry project)
    {
        if (_busy || project.Repository is null)
            return null;

        if (_gitView is { } current && !MatchesGitProject(project, current.Project))
        {
            if (current.IsBusy)
            {
                StatusText.Text = "Başka bir projeye geçmeden önce Git işleminin bitmesini bekleyin.";
                return null;
            }

            ResetGitPane(closePanel: false);
        }

        if (_gitView is null)
            GitPane.Content = _gitView = new(this, project);
        if (GitPane.IsClosed)
            WorkbenchDock.DockController.Restore(GitPane);
        WorkbenchDock.Activate(GitPane);
        var view = _gitView;
        await view.RefreshAsync();
        return ReferenceEquals(view, _gitView) ? view : null;
    }

    private static bool MatchesGitProject(ProjectEntry? selected, ProjectEntry opened) =>
        selected?.Repository is not null &&
        string.Equals(selected.RelativePath, opened.RelativePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(selected.Folder, opened.Folder, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(selected.Repository, opened.Repository, StringComparison.OrdinalIgnoreCase);

    private void CloseGitPaneForOtherProject()
    {
        if (!_closing.IsCancellationRequested && _gitView is { } view && !GitPane.IsClosed &&
            !MatchesGitProject(ProjectGrid.SelectedItem as ProjectEntry, view.Project))
            WorkbenchDock.DockController.Close(GitPane);
    }

    private void ResetGitPane(bool closePanel = true)
    {
        if (_gitView is null)
            return;

        _gitView.Release();
        _gitView = null;
        GitPane.Content = null;
        if (closePanel && !GitPane.IsClosed)
            WorkbenchDock.DockController.Close(GitPane);
        CloseGitDiff();
        GitDiff.ShowPreview(new([], ""));
    }

    private void WorkbenchPaneClosed(object sender, RoutedEventArgs e)
    {
        if (_restoringDockLayout)
            return;

        if (ProjectPane.IsClosed)
        {
            _drawerProject = null;
            ProjectDrawer.DataContext = null;
        }
        if (GitPane.IsClosed)
            CloseGitDiff();
    }

    internal void GitRepositoryChanged() => ScheduleWorkspaceRefresh();

    internal void ShowGitDiff()
    {
        if (GitPane.IsClosed)
            return;

        GitDiffPanel.Visibility = Visibility.Visible;
        UpdateCentralView();
    }

    private void CloseGitDiffClick(object sender, RoutedEventArgs e) => CloseGitDiff();

    private void CloseGitDiff()
    {
        GitDiffPanel.Visibility = Visibility.Collapsed;
        UpdateCentralView();
    }

    internal void SetGitDiffHeader(string title, string subtitle, string before = "Önce", string after = "Sonra")
    {
        GitDiffTitle.Text = title;
        GitDiffSubtitle.Text = subtitle;
        GitDiff.SetHeaders(before, after);
    }

    internal void SetGitDiffPreview(GitDiffPreview preview) => GitDiff.ShowPreview(preview);

    private void CaptureWorkspaceView()
    {
        if (_workspace is not { } workspace)
            return;

        _session.Remember(workspace.Root, (ProjectGrid.SelectedItem as ProjectEntry)?.RelativePath);
        _session.RememberExpandedFolders(workspace.Root, _expandedFolders);
        CaptureExpandedProjectGroups();
        _session.RememberView(workspace.Root, new(
            SelectedNavigationKey, SearchBox.Text, false, _drawerProject?.RelativePath,
            GitPane.IsClosed ? null : _gitView?.CaptureSession(GitDiffPanel.Visibility == Visibility.Visible),
            WorkbenchDock.ActiveDockItem?.Name, _expandedProjectGroups.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    private async Task RestoreWorkspacePanelsAsync(WorkspaceViewSession? view)
    {
        ProjectEntry? FindProject(string? path) =>
            _projects.FirstOrDefault(project => project.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));

        if (FindProject(view?.DetailsProject) is { } details)
            ShowProjectDrawer(details, activate: false);
        else
            CloseProjectDrawer();

        if (view?.Git is { } git && ProjectGrid.SelectedItem is ProjectEntry { Repository: not null } project &&
            string.Equals(git.Project, project.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            var gitView = new GitWorkbenchView(this, project);
            GitPane.Content = _gitView = gitView;
            if (GitPane.IsClosed)
                WorkbenchDock.DockController.Restore(GitPane);
            if (git.DiffOpen)
                ShowGitDiff();
            else
                CloseGitDiff();
            await gitView.RestoreSessionAsync(git);
        }
        else
        {
            if (!GitPane.IsClosed)
                WorkbenchDock.DockController.Close(GitPane);
            CloseGitDiff();
        }

        var active = view?.ActivePanel switch
        {
            nameof(ProjectPane) => ProjectPane,
            nameof(GitPane) => GitPane,
            nameof(ExplorerPane) => ExplorerPane,
            nameof(ProjectsPane) => ProjectsPane,
            _ => null
        };
        if (active is { IsClosed: false, IsAutoHidden: false })
            WorkbenchDock.DockController.Activate(active, false);
    }
}
