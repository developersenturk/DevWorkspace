using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class GitDiffView : UserControl
{
    public GitDiffView() => InitializeComponent();

    internal void SetHeaders(string before, string after)
    {
        BeforeColumn.Header = before;
        AfterColumn.Header = after;
    }

    internal void ShowPreview(GitDiffPreview preview)
    {
        Notice.Text = preview.Message;
        Notice.Visibility = preview.Message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DiffGrid.ItemsSource = preview.Rows;
        if (preview.Rows.Count > 0)
            DiffTable.ScrollIntoView(0);
    }
}
