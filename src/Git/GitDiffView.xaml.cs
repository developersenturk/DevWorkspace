using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class GitDiffView : UserControl
{
    private GitDiffPreview _preview = new([], "");
    private int _changeIndex = -1;
    internal (int Index, int Count) ChangePosition => (_changeIndex, _preview.ChangeStarts.Count);

    public GitDiffView() => InitializeComponent();

    internal void SetHeaders(string before, string after)
    {
        BeforeColumn.Header = before;
        AfterColumn.Header = after;
    }

    internal void ShowPreview(GitDiffPreview preview)
    {
        _preview = preview;
        _changeIndex = preview.ChangeStarts.Count > 0 ? 0 : -1;
        Notice.Text = preview.Message;
        Notice.Visibility = preview.Message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DiffGrid.ItemsSource = preview.Rows;
        UpdateNavigation();
        ScrollToChange();
    }

    internal void NavigateChange(int direction)
    {
        int next = _changeIndex + direction;
        if (next < 0 || next >= _preview.ChangeStarts.Count) return;
        _changeIndex = next;
        UpdateNavigation();
        ScrollToChange();
    }

    private void ScrollToChange()
    {
        var preview = _preview;
        int row = _changeIndex < 0 ? 0 : preview.ChangeStarts[_changeIndex];
        if (preview.Rows.Count == 0) return;
        DiffTable.FocusedRowHandle = row;
        DiffTable.ScrollIntoView(row);
        DiffTable.TopRowIndex = Math.Max(0, row - 3);
        // Virtualized rows can be measured later; never scroll an obsolete preview.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (ReferenceEquals(preview, _preview) && DiffTable.FocusedRowHandle == row)
            {
                DiffTable.ScrollIntoView(row);
                DiffTable.TopRowIndex = Math.Max(0, row - 3);
            }
        }));
    }

    private void UpdateNavigation()
    {
        PreviousChangeButton.IsEnabled = _changeIndex > 0;
        NextChangeButton.IsEnabled = _changeIndex >= 0 && _changeIndex < _preview.ChangeStarts.Count - 1;
        ChangePositionText.Text = Loc.Format("DiffChangePosition", _changeIndex + 1, _preview.ChangeStarts.Count);
    }

    private void PreviousChangeClick(object sender, RoutedEventArgs e) => NavigateChange(-1);
    private void NextChangeClick(object sender, RoutedEventArgs e) => NavigateChange(1);

    private void DiffKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Alt ||
            key is not (System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)) return;
        e.Handled = true;
        NavigateChange(key == System.Windows.Input.Key.Up ? -1 : 1);
    }
}
