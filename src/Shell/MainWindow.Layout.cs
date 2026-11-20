using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevExpress.Xpf.Grid;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class MainWindow
{
    private bool _comfortableDensity;
    private bool _restoringDockLayout;
    private byte[]? _defaultDockLayout;
    private readonly GridColumnWidth[] _defaultProjectColumnWidths;
    private readonly DispatcherTimer _saveColumnWidthsTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private static readonly DependencyPropertyDescriptor ColumnWidthProperty =
        DependencyPropertyDescriptor.FromProperty(BaseColumn.WidthProperty, typeof(GridColumn));

    private IEnumerable<GridColumn> ProjectColumns =>
        ProjectGrid.Columns.Where(column => !ReferenceEquals(column, VersionColumn));

    private void RestoreLayout()
    {
        WorkbenchLayout layout = _session.Layout ?? new();
        RestoreWindowLayout(layout.Window);
        ExplorerPane.ItemWidth = new(ClampWidth(layout.ExplorerWidth, 155, 400, 207));
        SetDensity(layout.Comfortable);
        GroupVersionsToggle.IsChecked = layout.GroupVersions;
        var columns = ProjectColumns.ToArray();
        if (layout.Columns is not { } widths)
            return;
        if (widths.Length == columns.Length - 1)
            columns = columns.Where(column => !ReferenceEquals(column, RemoteColumn)).ToArray();
        if (widths.Length != columns.Length)
            return;

        for (int i = 0; i < widths.Length; i++)
        {
            var width = widths[i];
            if (width is { Unit: "Pixel", Value: > 0 } && double.IsFinite(width.Value))
                columns[i].Width = new(width.Value, GridColumnUnitType.Pixel);
            if (width is not null)
                columns[i].Visible = width.Visible;
        }
        if (!ProjectColumns.Any(column => column.Visible))
            columns[0].Visible = true;
    }

    private void WatchProjectColumnWidths()
    {
        _saveColumnWidthsTimer.Tick += SaveProjectColumnWidths;
        foreach (var column in ProjectColumns)
            ColumnWidthProperty.AddValueChanged(column, ProjectColumnWidthChanged);
    }

    private void StopWatchingProjectColumnWidths()
    {
        _saveColumnWidthsTimer.Stop();
        _saveColumnWidthsTimer.Tick -= SaveProjectColumnWidths;
        foreach (var column in ProjectColumns)
            ColumnWidthProperty.RemoveValueChanged(column, ProjectColumnWidthChanged);
    }

    private void ProjectColumnWidthChanged(object? sender, EventArgs e)
    {
        _saveColumnWidthsTimer.Stop();
        _saveColumnWidthsTimer.Start();
    }

    private void SaveProjectColumnWidths(object? sender, EventArgs e)
    {
        _saveColumnWidthsTimer.Stop();
        _session.Layout = (_session.Layout ?? new()) with { Columns = CaptureProjectColumnWidths() };
        _session.Save();
    }

    private ColumnLayout[] CaptureProjectColumnWidths() => ProjectColumns
        .Select(column => new ColumnLayout(column.Width.Value, column.Width.UnitType.ToString(), column.Visible)).ToArray();

    private void CaptureLayout()
    {
        _session.Layout = new WorkbenchLayout(
            _comfortableDensity, GroupVersionsToggle.IsChecked == true,
            ClampWidth(ExplorerPane.ItemWidth.Value, 155, 400, 207),
            CaptureProjectColumnWidths(),
            CaptureWindowLayout(), CaptureDockLayout() ?? _session.Layout?.Dock);
    }

    private WindowLayout? CaptureWindowLayout()
    {
        Rect bounds = RestoreBounds;
        return bounds.IsEmpty ? _session.Layout?.Window :
            new(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
    }

    private void RestoreWindowLayout(WindowLayout? layout)
    {
        if (layout is null || !double.IsFinite(layout.Left) || !double.IsFinite(layout.Top) ||
            !double.IsFinite(layout.Width) || !double.IsFinite(layout.Height) || layout.Width <= 0 || layout.Height <= 0)
            return;

        Rect desktop = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        double width = ClampWidth(layout.Width, MinWidth, Math.Max(MinWidth, desktop.Width), Width);
        double height = ClampWidth(layout.Height, MinHeight, Math.Max(MinHeight, desktop.Height), Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp(layout.Left, desktop.Left, Math.Max(desktop.Left, desktop.Right - width));
        Top = Math.Clamp(layout.Top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - height));
        Width = width;
        Height = height;
        WindowState = layout.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private byte[]? CaptureDockLayout()
    {
        try
        {
            using var stream = new MemoryStream();
            WorkbenchDock.SaveLayoutToStream(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private void RestoreDockLayout() => TryRestoreDockLayout(_session.Layout?.Dock);

    private bool TryRestoreDockLayout(byte[]? saved)
    {
        if (saved is not { Length: > 0 and <= 1_000_000 })
            return false;

        _restoringDockLayout = true;
        try
        {
            using var stream = new MemoryStream(saved, writable: false);
            WorkbenchDock.RestoreLayoutFromStream(stream);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Xml.XmlException)
        {
            return false;
        }
        finally
        {
            _restoringDockLayout = false;
            // Dock snapshots contain captions from the previous session's language.
            ExplorerPane.Caption = Loc.Text("L768201CBF793");
            ProjectsPane.Caption = Loc.Text("L30BCB5BCCD3E");
            ProjectPane.Caption = Loc.Text("DashboardSelectedProject");
            GitPane.Caption = Loc.Text("LD11171088FD5");
        }
    }

    private static double ClampWidth(double width, double minimum, double maximum, double fallback) =>
        double.IsFinite(width) && width > 0 ? Math.Clamp(width, minimum, maximum) : fallback;

    private void ViewMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource))
            return;

        PopulateColumnsMenu();
        SaveFilterMenu.IsEnabled = !_busy && !_savingPreference && _workspace is not null;
        RemoveFilterMenu.IsEnabled = SaveFilterMenu.IsEnabled && CurrentProjectFilter is not null;
        ThemeMenu.Items.Clear();
        string active = ApplicationThemeHelper.ApplicationThemeName;
        var themes = LightweightThemeManager.Themes.Where(theme => theme.ShowInThemeSelector);
        foreach (var family in themes.GroupBy(ThemeFamily))
        {
            var category = new MenuItem { Header = family.Key, Icon = MenuGlyphExtension.Create("\uE790") };
            foreach (var theme in family.OrderBy(theme => !string.IsNullOrEmpty(theme.PaletteName))
                         .ThenBy(theme => theme.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new MenuItem
                {
                    Header = theme.DisplayName,
                    IsCheckable = true,
                    IsChecked = theme.Name == active,
                    Icon = MenuGlyphExtension.Create("\uE790"),
                    Tag = theme
                };
                item.Click += SelectTheme;
                category.Items.Add(item);
            }
            ThemeMenu.Items.Add(category);
        }
    }

    private void PopulateColumnsMenu()
    {
        ColumnsMenu.Items.Clear();
        int visibleCount = ProjectColumns.Count(column => column.Visible);
        foreach (var column in ProjectColumns)
        {
            var item = new MenuItem
            {
                Header = column.Header,
                Icon = MenuGlyphExtension.Create("\uE8A9"),
                IsCheckable = true,
                IsChecked = column.Visible,
                IsEnabled = !column.Visible || visibleCount > 1
            };
            item.Click += (_, _) =>
            {
                column.Visible = item.IsChecked;
                SaveProjectColumnWidths(null, EventArgs.Empty);
            };
            ColumnsMenu.Items.Add(item);
        }
        ColumnsMenu.Items.Add(new Separator());
        var reset = new MenuItem { Header = Loc.Text("LBA583019C9D8"), Icon = MenuGlyphExtension.Create("\uE777") };
        reset.Click += (_, _) =>
        {
            var columns = ProjectColumns.ToArray();
            for (int i = 0; i < columns.Length; i++)
                columns[i].Width = _defaultProjectColumnWidths[i];
            SaveProjectColumnWidths(null, EventArgs.Empty);
        };
        ColumnsMenu.Items.Add(reset);
    }

    private void PopulateSavedLayoutsMenu()
    {
        // Rebuild only when saved layouts change, never while the Windows popup is opening.
        // Removing submenu containers during SubmenuOpened can disrupt popup focus/capture.
        SavedLayoutsMenu.Items.Clear();
        foreach (var (name, layout) in _session.SavedLayouts.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem { Header = new TextBlock { Text = name }, Icon = MenuGlyphExtension.Create("\uE78B") };
            item.Click += (_, _) => ApplyDockLayout(layout, Loc.Format("L59C16893E1E3", name));
            SavedLayoutsMenu.Items.Add(item);
        }
        SavedLayoutsMenu.IsEnabled = SavedLayoutsMenu.HasItems;
        ResetLayoutMenu.IsEnabled = _defaultDockLayout is not null;
    }

    private void ResetDockLayoutClick(object sender, RoutedEventArgs e) =>
        ApplyDockLayout(_defaultDockLayout, Loc.Text("L0055A08BBF75"), keepOpenPanels: true);

    private void ApplyDockLayout(byte[]? layout, string status, bool keepOpenPanels = false)
    {
        bool projectWasOpen = !ProjectPane.IsClosed;
        bool gitWasOpen = !GitPane.IsClosed;
        if (!TryRestoreDockLayout(layout))
        {
            StatusText.Text = Loc.Text("L8E25556E1E12");
            return;
        }

        if (keepOpenPanels && projectWasOpen && ProjectPane.IsClosed)
            WorkbenchDock.DockController.Restore(ProjectPane);
        if (keepOpenPanels && gitWasOpen && GitPane.IsClosed)
            WorkbenchDock.DockController.Restore(GitPane);
        if (!ProjectPane.IsClosed && _drawerProject is null && ProjectGrid.SelectedItem is ProjectEntry project)
            ShowProjectDrawer(project, activate: false);
        if (!ProjectPane.IsClosed && _drawerProject is null)
            WorkbenchDock.DockController.Close(ProjectPane);
        if (_gitView is not { } git || !MatchesGitProject(ProjectGrid.SelectedItem as ProjectEntry, git.Project))
            WorkbenchDock.DockController.Close(GitPane);
        if (GitPane.IsClosed)
            CloseGitDiff();
        CaptureLayout();
        CaptureWorkspaceView();
        _session.Save();
        StatusText.Text = status;
    }

    private string? AskName(string title, string label)
    {
        var nameInput = new TextBox { MaxLength = 60, MinWidth = 290, Margin = new(0, 5, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(nameInput, label);
        var dialog = new ThemedWindow { Owner = this, Title = title, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var save = new ThemedWindowDialogButton
        {
            Content = Loc.Text("LC1FE2ECDFF02"), IsDefault = true, IsEnabled = false, MinWidth = 75, AllowCloseWindow = false
        };
        save.Click += (_, _) => dialog.DialogResult = true;
        nameInput.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(nameInput.Text);
        dialog.DialogButtons.Add(new ThemedWindowDialogButton
        {
            Content = Loc.Text("L39A8D28C1C6B"), IsCancel = true, DialogResult = MessageBoxResult.Cancel, MinWidth = 75
        });
        dialog.DialogButtons.Add(save);
        var form = new StackPanel { Margin = new(14) };
        form.Children.Add(new TextBlock { Text = label });
        form.Children.Add(nameInput);
        dialog.Content = form;
        dialog.Loaded += (_, _) => nameInput.Focus();
        return dialog.ShowDialog() == true ? nameInput.Text.Trim() : null;
    }

    private void SaveNamedLayoutClick(object sender, RoutedEventArgs e)
    {
        if (AskName(Loc.Text("L47328ED1E1F5"), Loc.Text("L4247BB7D33F3")) is not { } name)
            return;

        if (_session.SavedLayouts.ContainsKey(name) && MessageBox.Show(this, Loc.Format("LF01E1A1E1610", name),
                Loc.Text("L89F0107C5FEE"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (CaptureDockLayout() is { Length: > 0 and <= 1_000_000 } layout)
        {
            _session.SavedLayouts[name] = layout;
            _session.Save();
            PopulateSavedLayoutsMenu();
            StatusText.Text = Loc.Format("LE5D51EB0637C", name);
        }
        else
            StatusText.Text = Loc.Text("L592BB9311DF8");
    }

    private static string ThemeFamily(LightweightTheme theme) =>
        (string.IsNullOrEmpty(theme.CategoryName) ? theme.Name : theme.CategoryName) switch
        {
            "Win11" or "Win11System" or "Win11SystemColors" => "Windows 11",
            "Win10" or "Win10System" or "Win10SystemColors" => "Windows 10",
            "Office2019" or "Office2019System" => "Office 2019",
            "VS2019" or "VS2019System" => "Visual Studio 2019",
            var category => category
        };

    private void SelectTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: LightweightTheme theme })
            return;

        App.ApplyTheme(theme);
        _session.ThemeName = theme.Name;
        _session.Save();
    }

    private void CompactDensityClick(object sender, RoutedEventArgs e) => SetDensity(false);
    private void ComfortableDensityClick(object sender, RoutedEventArgs e) => SetDensity(true);

    private void SetDensity(bool comfortable)
    {
        _comfortableDensity = comfortable;
        ProjectTableView.RowMinHeight = comfortable ? 39 : 27;
        CompactMenu.IsChecked = !comfortable;
        ComfortableMenu.IsChecked = comfortable;
    }

    private void GroupingChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private ProjectEntry[] ComparisonSelection() => SelectedProjects().Take(3).ToArray();

    private ProjectEntry[] SelectedProjects() =>
        ProjectGrid?.SelectedItems?.OfType<ProjectEntry>().Where(project => !project.Preference.Archived).ToArray() ?? [];

    private void UpdateGroupSelection()
    {
        if (GroupSelectionButton is null)
            return;

        int count = SelectedProjects().Length;
        GroupSelectionButton.Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        GroupSelectionButton.IsEnabled = !_busy && !_savingPreference;
        GroupSelectionButton.Content = $"{count} projeyi grupla";
    }

    private void UpdateCompareSelection()
    {
        if (CompareSelectionButton is null)
            return;

        ProjectEntry[] selected = ComparisonSelection();
        CompareSelectionButton.Visibility = selected.Length == 2 && !selected[0].Folder.Equals(selected[1].Folder, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CompareSelectionClick(object sender, RoutedEventArgs e)
    {
        ProjectEntry[] selected = ComparisonSelection();
        if (_workspace is Workspace workspace && selected.Length == 2 && !selected[0].Folder.Equals(selected[1].Folder, StringComparison.OrdinalIgnoreCase))
            new ComparisonWindow(workspace, selected[0], selected[1]) { Owner = this }.ShowDialog();
    }
}
