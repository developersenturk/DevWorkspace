using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private readonly SessionState _session = SessionState.Load();
    private bool _savingPreference;

    private string[] CurrentRecent() => _workspace is null ? [] : _session.For(_workspace.Root).RecentProjects ?? [];

    private void QuickOpenClick(object sender, RoutedEventArgs e) => QuickOpen();

    private void QuickOpen()
    {
        if (_busy || _projects.Count == 0)
            return;

        ProjectPickerAction[] actions =
        [
            new("Klasörü aç", "folder explorer", project => Directory.Exists(project.Folder),
                project => OpenPath(project.Folder)),
            new("Terminal aç", "terminal powershell shell", project => Directory.Exists(project.Folder), OpenProjectTerminal),
            new("Git değişiklikleri", "git diff değişiklik", project => project.Repository is not null, OpenProjectDiff),
            new("NuGet bulguları", "nuget paket güvenlik", ProjectIntegrations.HasNuGet,
                project => ShowProjectTools(ProjectToolView.NuGet, project)),
            new("Proje işlemleri", "build test run derle çalıştır", project => Directory.Exists(project.Folder),
                project => ShowProjectTools(ProjectToolView.Commands, project)),
            new("Proje ortamı", "ortam environment sdk", project => Directory.Exists(project.Folder), OpenProjectEnvironment)
        ];
        var picker = new ProjectPickerWindow(_projects, CurrentRecent(), "Komut paleti", "Uygula", actions: actions) { Owner = this };
        if (picker.ShowDialog() == true && picker.Chosen is { } project)
        {
            SearchBox.Text = "";
            BuildNavigation("all");
            ProjectGrid.SelectedItem = project;
            FocusProject(project);
            if (picker.ChosenAction is { } action)
                action.Execute(project);
            else
                OpenProject(project);
        }
    }

    private void OpenProjectTerminal(ProjectEntry project)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo")
            {
                UseShellExecute = true,
                WorkingDirectory = project.Folder
            });
        }
        catch (Win32Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OpenProjectDiff(ProjectEntry project)
    {
        if (EnsureTool(DevelopmentTool.Git) && await OpenGitWorkbenchAsync(project) is { } view)
            view.OpenFirstChange();
    }

    private void OpenProject(ProjectEntry project)
    {
        string path = project.Kind is "Visual Studio" or ".NET / MSBuild" ? project.EntryFile ?? project.Folder : project.Folder;
        if (OpenPath(path, offerEditor: true))
        {
            if (_workspace is { } workspace && IsSolution(path))
                _session.RememberSolution(workspace.Root, path);
            RememberOpened(project);
        }
    }

    private void RememberOpened(ProjectEntry project)
    {
        if (_workspace is null)
            return;

        _session.Remember(_workspace.Root, project.RelativePath, opened: true);
        _session.Save();
        BuildNavigation(SelectedNavigationKey);
    }

    private static bool IsSolution(string path) =>
        Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    private string? LastSolutionPath()
    {
        if (_workspace is not { } workspace)
            return null;

        var session = _session.For(workspace.Root);
        if (session.LastSolution is { } saved)
            return IsSolution(saved) && File.Exists(saved) && !_archivedProjects.Any(project =>
                string.Equals(project.EntryFile, saved, StringComparison.OrdinalIgnoreCase)) ? saved : null;

        foreach (string relativePath in session.RecentProjects ?? [])
        {
            var project = _projects.FirstOrDefault(entry => entry.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            if (project?.EntryFile is { } entryFile && IsSolution(entryFile) && File.Exists(entryFile))
                return entryFile;
        }
        return null;
    }

    private void UpdateLastSolutionButton()
    {
        string? path = SelectedNavigationKey == "recent" ? LastSolutionPath() : null;
        LastSolutionButton.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        LastSolutionButton.IsEnabled = !_busy && path is not null;
        LastSolutionButton.ToolTip = path;
    }

    private void LastSolutionClick(object sender, RoutedEventArgs e)
    {
        if (_busy || LastSolutionPath() is not { } path)
            return;

        var project = _projects.FirstOrDefault(entry => string.Equals(entry.EntryFile, path, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
            OpenProject(project);
        else
            OpenPath(path, offerEditor: true);
    }

    private async void FavoriteClick(object sender, RoutedEventArgs e) =>
        await SavePreferenceAsync(current => current with { Favorite = !current.Favorite });

    private Task SavePreferenceAsync(Func<ProjectPreference, ProjectPreference> change) =>
        ProjectGrid.SelectedItem is ProjectEntry project ? SavePreferencesAsync([project], change) : Task.CompletedTask;

    private bool CanChangeProjectArchive => !_busy && !_savingPreference && _workspaceCheck is null &&
        _dashboardScan is null && _gitView?.IsBusy != true;

    private async void ChangeProjectArchive(ProjectEntry project, bool archived)
    {
        if (CanChangeProjectArchive)
            await SavePreferencesAsync([project], current => current with { Archived = archived }, archiveChange: true);
    }

    private void GroupSelectionClick(object sender, RoutedEventArgs e) => OpenBulkGroupEditor(SelectedProjects());

    private async void OpenBulkGroupEditor(ProjectEntry[] selection)
    {
        if (_busy || _savingPreference || _workspace is not Workspace workspace || selection.Length < 2)
            return;

        var dialog = new ProjectDetailWindow(workspace, selection[0], ProjectDetailView.Organization, _projects) { Owner = this };
        dialog.ConfigureBulkOrganization(selection);
        if (dialog.ShowDialog() == true)
        {
            string group = dialog.Group;
            await SavePreferencesAsync(selection, current => current with { Group = group }, group);
        }
    }

    private async Task SavePreferencesAsync(ProjectEntry[] selection, Func<ProjectPreference, ProjectPreference> change,
        string? bulkGroup = null, bool archiveChange = false)
    {
        if (_savingPreference || _busy || _workspace is not Workspace workspace)
            return;

        _savingPreference = true;
        if (archiveChange)
            SetBusy(true);
        UpdateGroupSelection();
        try
        {
            string[] paths = selection.Select(project => project.RelativePath).ToArray();
            Workspace updated = await Task.Run(() => workspace.SavePreferences(paths, change));
            if (_closing.IsCancellationRequested || _workspace?.Root != updated.Root)
                return;

            _workspace = updated;
            if (archiveChange)
            {
                InvalidateWorkspaceChecks();
                if (updated.PreferenceFor(paths[0]).Archived)
                {
                    _projects = _projects.Where(project => !project.RelativePath.Equals(paths[0], StringComparison.OrdinalIgnoreCase)).ToArray();
                    _archivedProjects = [.. _archivedProjects, selection[0] with
                    {
                        SavedPreference = updated.PreferenceFor(paths[0]), Repository = null, Git = new(GitState.None)
                    }];
                    BuildNavigation(SelectedNavigationKey);
                }
                _savingPreference = false;
                SetBusy(false);
                await RefreshAsync();
                return;
            }
            string? selected = (ProjectGrid.SelectedItem as ProjectEntry)?.RelativePath;
            string key = SelectedNavigationKey;
            _projects = _projects.Select(entry => entry with { SavedPreference = updated.PreferenceFor(entry.RelativePath) }).ToArray();
            if (bulkGroup is { Length: > 0 })
            {
                GroupVersionsToggle.IsChecked = true;
                key = $"group:{bulkGroup}";
            }
            BuildNavigation(key);
            var current = _visibleProjects.FirstOrDefault(entry => entry.RelativePath == (bulkGroup is null ? selected : paths[0]));
            if (current is not null)
            {
                if (bulkGroup is null)
                    ProjectGrid.SelectedItem = current;
                else
                    FocusProject(current);
            }
            StatusText.Text = bulkGroup switch
            {
                null => "Proje tercihleri kaydedildi.",
                "" => $"{paths.Length} proje grubundan çıkarıldı.",
                _ => $"{paths.Length} proje “{bulkGroup}” grubuna eklendi."
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusText.Text = $"Proje tercihleri kaydedilemedi. {ex.Message}";
        }
        finally
        {
            _savingPreference = false;
            if (archiveChange && _busy && !_closing.IsCancellationRequested)
                SetBusy(false);
            UpdateGroupSelection();
        }
    }

    private void CompareProjectClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is not Workspace workspace || ProjectGrid.SelectedItem is not ProjectEntry left)
            return;

        var choices = _projects.Where(project => !project.Folder.Equals(left.Folder, StringComparison.OrdinalIgnoreCase));
        var picker = new ProjectPickerWindow(choices, CurrentRecent(), $"Karşılaştır · {left.Name}", "Karşılaştır", left) { Owner = this };
        if (picker.ShowDialog() == true && picker.Chosen is { } right)
            new ComparisonWindow(workspace, left, right) { Owner = this }.ShowDialog();
    }

    private void PreviewMoveClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is Workspace workspace && ProjectGrid.SelectedItem is ProjectEntry { IsWorkspaceRoot: false } project)
            new MovePreviewWindow(workspace, project) { Owner = this }.ShowDialog();
    }

    private void ProjectCommandsClick(object sender, RoutedEventArgs e) => ShowProjectTools(ProjectToolView.Commands);
    private void ProjectHealthClick(object sender, RoutedEventArgs e) => ShowProjectTools(ProjectToolView.Health);

    private void ShowProjectTools(ProjectToolView view, ProjectEntry? project = null, HealthResult? healthReport = null)
    {
        project ??= ProjectGrid.SelectedItem as ProjectEntry;
        if (_workspace is not Workspace workspace || project is null || project.Preference.Archived)
            return;

        var window = new ProjectToolsWindow(workspace, project, view, GetNuGetState(project), healthReport) { Owner = this };
        window.ShowDialog();
        if (window.UpdatedWorkspace is Workspace updated && _workspace?.Root == updated.Root)
            _workspace = updated;
        if (window.OpenGitAfterClose)
        {
            NavigateFromDashboard(GitRepositoriesKey, project);
            OpenGitWorkbench(project);
        }
    }
}


