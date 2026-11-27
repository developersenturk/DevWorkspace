using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Grid;
using DevWorkspace;

internal static class GitAmendUiChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var language in new[] { "tr", "en" })
        {
            Loc.Initialize(language);
            var dialog = new GitAmendWindow("Sample", "0123456789abcdef", "subject\n\nbody", 2);
            check(dialog.Message == "subject\n\nbody", "Amend preloads the full multiline message");
            var input = (TextBox)dialog.FindName("MessageInput");
            var apply = (Button)dialog.FindName("ApplyButton");
            check(apply.IsEnabled, "Existing message can be amended without staging files");
            input.Text = "   ";
            check(!apply.IsEnabled, "Whitespace-only messages disable Amend");
            dialog.Close();
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var window = new MainWindow();
        typeof(MainWindow).GetMethod("StopWatchingProjectColumnWidths", flags)!.Invoke(window, null);
        var session = (SessionState)typeof(MainWindow).GetField("_session", flags)!.GetValue(window)!;
        session.Layout = new(Columns: Enumerable.Range(0, 6).Select(index => new ColumnLayout(260 + index * 20, "Pixel")).ToArray());
        typeof(MainWindow).GetMethod("RestoreLayout", flags)!.Invoke(window, null);
        var grid = (GridControl)window.FindName("ProjectGrid");
        check(grid.Columns.Single(column => column.FieldName == "Git.RemoteSummary").Width.Value == 340, "Old saved layouts preserve remote column widths");
        check(grid.Columns.Single(column => column.FieldName == "ChangedFiles").Width.Value == 360, "Old saved layouts preserve changed-file column widths");
        check(grid.Columns.Single(column => column.FieldName == "Git.PushLabel").Visible, "New push status is visible with old saved layouts");
        window.Close();
    }
}
