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
            ProjectMenu.Items.Add(new MenuItem { Header = "Bir proje seçin", IsEnabled = false });
    }

    private void PopulateProjectMenu(ItemsControl menu, Workspace workspace, ProjectEntry project, ProjectEntry[] pair)
    {
        if (pair.Length > 1 && pair.Contains(project))
        {
            var group = new MenuItem
            {
                Header = $"Seçili {pair.Length} projeyi grupla…",
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

        bool folderExists = Directory.Exists(project.Folder);
        Add(project.Kind == "Visual Studio" ? "Çözümü aç" : "Projeyi aç", "\uE8A7",
            () => OpenProjectClick(this, new()), folderExists);
        Add("Klasörü aç", "\uE838", () => ExplorerClick(this, new()), folderExists);
        Add("Yolu kopyala", "\uE8C8", () => CopyPathClick(this, new()));
        bool favorite = project.Preference.Favorite;
        Add(favorite ? "Favoriden çıkar" : "Favoriye ekle", favorite ? "\uE735" : "\uE734",
            () => FavoriteClick(this, new()));
        menu.Items.Add(new Separator());
        if (project.Repository is not null)
        {
            Add("Git geçmişi", MenuGlyphExtension.GitHistory, () => new GitHistoryWindow(project) { Owner = this }.ShowDialog());
            Add("Git işlemleri", MenuGlyphExtension.GitOperations, () => OpenGitWorkbench(project));
        }
        else
            Add("Git deposu oluştur…", "\uE710", () => InitializeProjectGitAsync(project), folderExists && !_initializingGit);
        Add("README ve belgeler", "\uE8A5", () => OpenProjectDetail(ProjectDetailView.Documents), folderExists);
        Add("Proje amacı ve konumu", "\uE946", () => OpenProjectDetail(ProjectDetailView.Purpose));

        menu.Items.Add(new Separator());
        Add("Proje işlemleri", "\uE713", () => ProjectCommandsClick(this, new()), folderExists);
        Add("Sağlık kontrolü", "\uE9D9", () => ProjectHealthClick(this, new()));
        Add("Grup ve etiket", "\uE8EC", () => OpenProjectDetail(ProjectDetailView.Organization));
        if (pair.Length == 2 && pair.Contains(project) && !pair[0].Folder.Equals(pair[1].Folder, StringComparison.OrdinalIgnoreCase))
            Add("Seçili iki sürümü karşılaştır", "\uE8AB", () => new ComparisonWindow(workspace, pair[0], pair[1]) { Owner = this }.ShowDialog());
        else
            Add("Bir sürümle karşılaştır…", "\uE8AB", () => CompareProjectClick(this, new()));
        Add("Taşıma planı", "\uE8DE", () => PreviewMoveClick(this, new()), folderExists && project.LinkPath is null && !project.IsWorkspaceRoot);
        if (project.LinkPath is not null)
            Add("Bağlantıyı kaldır", "\uE738", () => UnlinkProjectClick(this, new()));
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
        if (MessageBox.Show(this, $"Bu klasörde yerel Git deposu oluşturulsun mu?\n\n{project.Folder}\n\nVarsa .gitignore korunur, yoksa proje türüne uygun bir dosya hazırlanır. İlk commit veya uzak depo bağlantısı ayrıca yapılır.",
            "Git deposu oluştur", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _initializingGit = true;
        try
        {
            await GitProjectOperations.InitializeAsync(project.Folder, project.Kind, _closing.Token);
            if (_workspace?.Root == workspace)
                await RefreshAsync();
            StatusText.Text = "Yerel Git deposu oluşturuldu.";
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
