using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DevWorkspace;

public partial class MainWindow
{
    private IReadOnlyList<ProjectComponent> _projectComponents = [];

    private ProjectEntry? _drawerProject;
    private ProjectEntry? _firstProjectClick;
    private readonly Dictionary<string, NuGetScanState> _nuGetStates = new(StringComparer.OrdinalIgnoreCase);

    private void ProjectRowLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
            e.OriginalSource is not DependencyObject source)
        {
            _firstProjectClick = null;
            return;
        }

        var hit = ProjectTableView.CalcHitInfo(source);
        var project = hit.InRowCell ? ProjectGrid.GetRow(hit.RowHandle) as ProjectEntry : null;
        if (e.ClickCount > 1 && (project is not null || ProjectGrid.IsGroupRowHandle(hit.RowHandle)))
        {
            bool open = e.ClickCount == 2 && project is not null && ReferenceEquals(_firstProjectClick, project);
            _firstProjectClick = null;
            e.Handled = true;
            if (open && project is not null)
            {
                if (SelectedNavigationKey == GitRepositoriesKey && project.Repository is not null)
                    OpenGitWorkbench(project);
                else
                    OpenProject(project);
            }
            return;
        }

        _firstProjectClick = e.ClickCount == 1 ? project : null;
        if (project is not null)
            ShowProjectDrawer(project);
    }

    private void ShowProjectDrawer(ProjectEntry project, bool activate = true)
    {
        _drawerProject = project;
        ProjectDrawer.DataContext = project;
        DrawerEntryName.Text = Path.GetFileName(project.EntryFile) ?? "";
        DrawerEntryName.Visibility = project.EntryFile is null ? Visibility.Collapsed : Visibility.Visible;
        DrawerOpenButton.Visibility = project.EntryFile is null ? Visibility.Collapsed : Visibility.Visible;
        DrawerOpenButton.Content = project.Kind switch
        {
            "Visual Studio" => "Çözümü aç",
            ".NET / MSBuild" => "Projeyi aç",
            _ => "Dosyayı aç"
        };
        string description = _workspace?.DescriptionFor(project.RelativePath) ?? "";
        DrawerPurposeText.Text = description;
        DrawerPurposeText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
        DrawerGitDetail.Visibility = string.IsNullOrWhiteSpace(project.Git.Detail) ? Visibility.Collapsed : Visibility.Visible;
        bool folderExists = Directory.Exists(project.Folder);
        DrawerBranch.Visibility = string.IsNullOrWhiteSpace(project.Git.Branch) ? Visibility.Collapsed : Visibility.Visible;
        DrawerOpenFolderButton.IsEnabled = folderExists;
        DrawerOpenFolderButton.Style = (Style)ProjectDrawer.FindResource(project.EntryFile is null ? "DrawerPrimary" : "DrawerAction");
        DrawerGitButton.Visibility = project.Repository is null ? Visibility.Collapsed : Visibility.Visible;
        bool active = !project.Preference.Archived;
        DrawerGitInitButton.Visibility = active && project.Repository is null && folderExists
            ? Visibility.Visible : Visibility.Collapsed;
        bool hasActions = project.Repository is not null && folderExists;
        bool hasCompose = active && ProjectIntegrations.HasCompose(project);
        var nuGet = active ? GetNuGetState(project) : null;
        DrawerNuGetCard.DataContext = nuGet;
        DrawerNuGetCard.Visibility = nuGet is null ? Visibility.Collapsed : Visibility.Visible;
        DrawerActionsButton.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        DrawerComposeButton.Visibility = hasCompose ? Visibility.Visible : Visibility.Collapsed;
        DrawerCommandsButton.Visibility = folderExists ? Visibility.Visible : Visibility.Collapsed;
        DrawerToolsCard.Visibility = active && folderExists ? Visibility.Visible : Visibility.Collapsed;
        var components = _projectComponents
            .Where(component => component.OwnerFolder.Equals(project.Folder, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(component => component.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(component => component.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(component => new
            {
                Component = component,
                Detail = $"{component.Role} · {component.Kind} · {Path.GetRelativePath(project.Folder, component.Folder)}",
                Action = _projects.Any(row => row.Folder.Equals(component.Folder, StringComparison.OrdinalIgnoreCase))
                    ? "Satıra git" : "Ayrı satır olarak aç"
            }).ToArray();
        DrawerComponents.ItemsSource = components;
        DrawerComponentsCard.Visibility = components.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (ProjectPane.IsClosed)
            WorkbenchDock.DockController.Restore(ProjectPane);
        if (activate)
            WorkbenchDock.DockController.Activate(ProjectPane, false);
    }

    private async void DrawerShowComponentClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: ProjectComponent component })
            return;
        if (await AttachProjectsAsync([component.Folder]) is { } project)
            ShowProjectDrawer(project);
    }

    private void RefreshProjectDrawer()
    {
        if (_drawerProject is not { } previous)
            return;

        var current = _visibleProjects.FirstOrDefault(project =>
            project.RelativePath.Equals(previous.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            project.Folder.Equals(previous.Folder, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            CloseProjectDrawer();
        else
            ShowProjectDrawer(current, activate: false);
    }

    private void CloseProjectDrawer()
    {
        if (!ProjectPane.IsClosed)
            WorkbenchDock.DockController.Close(ProjectPane);
    }

    private void OpenDrawerAfterKeyboardNavigation(Key key)
    {
        if (key is not (Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown) ||
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!_busy && ProjectGrid.IsKeyboardFocusWithin &&
                ProjectGrid.GetRow(ProjectTableView.FocusedRowHandle) is ProjectEntry project)
                ShowProjectDrawer(project);
        });
    }

    private void DrawerOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is not { EntryFile: { } entry } project)
            return;

        if (project.Kind is "Visual Studio" or ".NET / MSBuild")
            OpenProject(project);
        else if (OpenPath(entry, offerEditor: true))
            RememberOpened(project);
    }

    private void DrawerOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { } project && OpenPath(project.Folder))
            RememberOpened(project);
    }

    private void DrawerCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { } project)
            CopyProjectPath(project);
    }

    private void DrawerGitClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { Repository: not null } project)
            OpenGitWorkbench(project);
    }

    private void DrawerGitInitClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { Repository: null } project)
            InitializeProjectGitAsync(project);
    }

    private void DrawerPurposeClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { } project)
            OpenProjectDetail(ProjectDetailView.Purpose, project);
    }

    private void DrawerDocumentsClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { } project)
            OpenProjectDetail(ProjectDetailView.Documents, project);
    }

    private void DrawerToolClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { } project && sender is Button { Tag: string tag } &&
            Enum.TryParse<ProjectToolView>(tag, out var view))
            ShowProjectTools(view, project);
    }

    private async void DrawerNuGetScanClick(object sender, RoutedEventArgs e)
    {
        if (_drawerProject is { } project && GetNuGetState(project) is { CanScan: true } state && await EnsureSdkAsync())
            await state.ScanAsync(project, _closing.Token);
    }

    private NuGetScanState? GetNuGetState(ProjectEntry project)
    {
        if (!ProjectIntegrations.HasNuGet(project))
            return null;
        string path = project.EntryFile!;
        if (!_nuGetStates.TryGetValue(path, out var state))
        {
            state = new(_session.NuGetScans.GetValueOrDefault(path), result =>
            {
                _session.NuGetScans[path] = result;
                RememberWorkspaceActivity();
                _session.Save();
            }, _closing.Token);
            state.PropertyChanged += (_, _) =>
            {
                if (state.CanScan)
                    UpdateDashboard();
            };
            _nuGetStates.Add(path, state);
        }
        return state;
    }
}
