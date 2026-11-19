using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DevExpress.Xpf.Editors;

namespace DevWorkspace;

public partial class MainWindow
{
    private ContextMenu? _explorerContextMenu;
    private ExplorerFolderEdit? _explorerEdit;
    private bool _savingExplorerFolder;
    private const string NewFolderKey = "draft:folder";

    private sealed class ExplorerFolderEdit(string? path, bool rename, string name)
    {
        public string? Path { get; } = path;
        public bool Rename { get; } = rename;
        public string Name { get; set; } = name;
        public string Key => Rename ? $"folder:{Path}" : NewFolderKey;
    }

    private void AddExplorerFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavigationItem item })
            BeginExplorerEdit(item, rename: false);
    }

    private void ExplorerFolderMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavigationItem item } anchor)
            OpenExplorerFolderMenu(ExplorerRowAnchor(anchor), item, PlacementMode.Bottom);
    }

    private static FrameworkElement ExplorerRowAnchor(FrameworkElement button)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(button); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is FrameworkElement { Name: "FolderRow" } row)
                return row;
        return button;
    }

    private void ExplorerFolderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NavigationItem { HasFolderActions: true } item } anchor)
        {
            e.Handled = true;
            OpenExplorerFolderMenu(anchor, item, PlacementMode.MousePoint);
        }
    }

    private void ExplorerNavigationKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F2 && Navigation.SelectedItem is NavigationItem { FolderPath: not null, IsEditing: false } folder)
        {
            e.Handled = true;
            BeginExplorerEdit(folder, rename: true);
            return;
        }
        if ((key == Key.Apps || key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift) &&
            Navigation.SelectedItem is NavigationItem { HasFolderActions: true } item)
        {
            e.Handled = true;
            OpenExplorerFolderMenu(Navigation, item, PlacementMode.Center);
        }
    }

    private void OpenExplorerFolderMenu(FrameworkElement anchor, NavigationItem item, PlacementMode placement)
    {
        if (_workspace is not { } workspace || _busy || _savingExplorerFolder || !item.HasFolderActions)
            return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = placement };
        void Add(string label, Action action, bool enabled = true)
        {
            var command = new MenuItem { Header = label, IsEnabled = enabled };
            command.Click += (_, _) =>
            {
                if (_workspace?.Root == workspace.Root)
                    action();
            };
            menu.Items.Add(command);
        }

        Add(item.IsFolderSection ? Loc.Text("L9965C7388A0A") : Loc.Text("L40EDE421B8B0"), () => BeginExplorerEdit(item, rename: false));
        if (item.FolderPath is { } path)
        {
            string folder = Path.Combine(workspace.Root, path);
            string? parent = Path.GetDirectoryName(path);
            string[] siblings = string.IsNullOrEmpty(parent)
                ? _folders.Where(entry => entry.IsVisible && string.IsNullOrEmpty(Path.GetDirectoryName(entry.Name)))
                    .Select(entry => entry.Name).ToArray()
                : OrderedFolderChildren(_folderChildren.GetValueOrDefault(parent) ?? []).ToArray();
            int index = Array.FindIndex(siblings, sibling => sibling.Equals(path, StringComparison.OrdinalIgnoreCase));
            menu.Items.Add(new Separator());
            Add(Loc.Text("L8D6DB04381D4"), () => BeginExplorerEdit(item, rename: true));
            Add(Loc.Text("L6820B3BD2486"), () => MoveExplorerFolder(path, siblings, index, -1), index > 0);
            Add(Loc.Text("L9B1BE60F99F3"), () => MoveExplorerFolder(path, siblings, index, 1), index >= 0 && index < siblings.Length - 1);
            Add(Loc.Text("L6FDD2C3F0393"), () => ChangeExplorerFolder(workspace => workspace.SetExplorerFolderHidden(path, true), Loc.Text("LDC9F639333AD")));
            menu.Items.Add(new Separator());
            Add(Loc.Text("L37E690A92E5C"), () => OpenPath(folder), Directory.Exists(folder));
            Add(Loc.Text("L72C32847EB3E"), () => CopyExplorerFolderPath(folder));
        }
        else
        {
            var hidden = new MenuItem { Header = Loc.Text("LE74226DEA03F"), IsEnabled = _folders.Any(folder => !folder.IsVisible) };
            foreach (WorkspaceFolder folder in _folders.Where(folder => !folder.IsVisible))
            {
                var show = new MenuItem { Header = folder.Name, ToolTip = Loc.Format("LE9F2852778E4", folder.Label) };
                show.Click += (_, _) =>
                {
                    if (_workspace?.Root == workspace.Root)
                        ChangeExplorerFolder(current => current.SetExplorerFolderHidden(folder.Name, false), Loc.Text("L44E6B7941652"));
                };
                hidden.Items.Add(show);
            }
            menu.Items.Add(hidden);
            menu.Items.Add(new Separator());
            Add(Loc.Text("L47E1F9FB8C0D"), () => OpenPath(workspace.Root));
            Add(Loc.Text("L72C32847EB3E"), () => CopyExplorerFolderPath(workspace.Root));
        }

        _explorerContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_explorerContextMenu, menu)) _explorerContextMenu = null; };
        menu.IsOpen = true;
    }

    private void MoveExplorerFolder(string path, string[] siblings, int index, int offset) =>
        ChangeExplorerFolder(workspace => workspace.MoveExplorerFolder(path, siblings[index + offset], offset < 0, siblings),
            Loc.Text("L71858DF0B5A0"));

    private async void ChangeExplorerFolder(Func<Workspace, Workspace> change, string success)
    {
        string? error = await SaveExplorerChangeAsync(change);
        StatusText.Text = error ?? success;
    }

    private void CopyExplorerFolderPath(string path)
    {
        try { Clipboard.SetText(path); }
        catch (ExternalException) { StatusText.Text = Loc.Text("L27388BFE0C95"); }
    }

    private void BeginExplorerEdit(NavigationItem item, bool rename)
    {
        if (_workspace is not { } workspace || _busy || _savingExplorerFolder)
            return;

        string name = item.Label;
        if (!rename)
        {
            string parent = item.FolderPath is { } path ? Path.Combine(workspace.Root, path) : workspace.Root;
            string defaultName = Loc.Text("L8D62DD79B534");
            name = defaultName;
            for (int i = 2; Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name)); i++)
                name = $"{defaultName} ({i})";
        }

        var edit = new ExplorerFolderEdit(item.FolderPath, rename, name);
        _explorerEdit = edit;
        BuildNavigation(SelectedNavigationKey);
        if (_navigationNodes.GetValueOrDefault(edit.Key) is { } row && Navigation.GetNodeByContent(row) is { } node)
            Navigation.ScrollIntoView(node);
    }

    private void ExplorerInlineEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextEdit { DataContext: NavigationItem item } editor)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (editor.IsLoaded && editor.IsVisible && ReferenceEquals(item.FolderEdit, _explorerEdit))
            {
                editor.Focus();
                editor.SelectAll();
            }
        });
    }

    private TextEdit? FindInlineExplorerEditor(object source)
    {
        if (_explorerEdit is not { } edit)
            return null;
        for (DependencyObject? element = source as Visual; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextEdit { DataContext: NavigationItem item } editor && ReferenceEquals(item.FolderEdit, edit))
                return editor;
        return null;
    }

    private void CancelExplorerEdit()
    {
        if (_savingExplorerFolder)
            return;

        _explorerEdit = null;
        BuildNavigation(SelectedNavigationKey);
        Navigation.Focus();
    }

    private async Task SaveExplorerEditAsync(string? text)
    {
        if (_explorerEdit is not { } edit || _busy || _savingExplorerFolder)
            return;

        string name = (text ?? "").Trim();
        if (!edit.Rename && name.Length == 0)
        {
            StatusText.Text = Loc.Text("LE506D5B94E13");
            return;
        }

        string? path = edit.Path;
        string? error = await SaveExplorerChangeAsync(
            workspace => edit.Rename ? workspace.SetExplorerFolderLabel(path!, name) : workspace.AddExplorerFolder(path, name),
            () =>
            {
                _explorerEdit = null;
                if (!edit.Rename && path is not null)
                    _expandedFolders.Add(path);
            });
        if (error is not null)
        {
            StatusText.Text = error;
            return;
        }

        string key = edit.Rename ? SelectedNavigationKey : "folder:" + Path.Combine(path ?? "", name);
        if (_navigationNodes.GetValueOrDefault(edit.Key) is { IsEditing: true })
            BuildNavigation(key);
        if (_navigationNodes.GetValueOrDefault(key) is { } row)
        {
            Navigation.SelectedItem = row;
            if (Navigation.GetNodeByContent(row) is { } node)
                Navigation.ScrollIntoView(node);
        }
        Navigation.Focus();
        StatusText.Text = edit.Rename ? Loc.Text("L27A279B6608A") : Loc.Text("LA2073F3DB266");
    }

    private async Task<string?> SaveExplorerChangeAsync(Func<Workspace, Workspace> change, Action? beforeRefresh = null)
    {
        if (_workspace is not { } workspace || _busy || _savingExplorerFolder)
            return Loc.Text("LBC58F9B58E8B");

        _savingExplorerFolder = true;
        try
        {
            Workspace updated = await Task.Run(() => change(workspace));
            if (_closing.IsCancellationRequested || _workspace?.Root != workspace.Root)
                return Loc.Text("L5385865DCF72");

            _workspace = updated;
            beforeRefresh?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return ex.Message;
        }
        finally
        {
            _savingExplorerFolder = false;
        }
        await RefreshAsync();
        return null;
    }
}
