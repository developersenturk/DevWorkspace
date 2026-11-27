using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevExpress.Xpf.Grid;
using DevWorkspace;

internal static class GitDiffNavigationChecks
{
    internal static GitDiffPreview Fixture()
    {
        var patch = new System.Text.StringBuilder("@@ -1,600 +1,600 @@\n");
        for (int line = 1; line <= 600; line++)
            patch.Append(line switch
            {
                120 => "-old value\n+new value\n",
                300 => "+inserted value\n",
                550 => "-deleted value\n",
                _ => $" unchanged line {line}\n"
            });
        return GitDiffPreview.FromPatch(patch.ToString());
    }

    internal static void Run(Action<bool, string> check)
    {
        var preview = Fixture();
        check(preview.ChangeStarts.SequenceEqual(new[] { 119, 299, 549 }), "Replacement, addition and deletion form separate blocks");
        var view = new GitDiffView();
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        var table = (TableView)view.FindName("DiffTable");
        var next = (Button)view.FindName("NextChangeButton");
        var previous = (Button)view.FindName("PreviousChangeButton");
        void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        view.ShowPreview(preview); Pump();
        check(view.ChangePosition == (0, 3) && table.FocusedRowHandle == 119, "Opening a long file jumps to the first change, not line one");
        check(table.TopRowIndex >= 115 && table.TopRowIndex <= 119, "Change is positioned near the top with a few context lines");
        check(!previous.IsEnabled && next.IsEnabled, "First change boundary");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        check(view.ChangePosition == (1, 3) && table.FocusedRowHandle == 299, "Next button jumps to the second change in the same file");
        view.NavigateChange(1); Pump();
        check(table.FocusedRowHandle == 549 && !next.IsEnabled, "Last change is reachable");
        view.NavigateChange(1);
        check(view.ChangePosition.Index == 2, "Next does not wrap or open another file");
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        check(view.ChangePosition.Index == 1 && table.FocusedRowHandle == 299, "Previous button returns to the previous block");
        view.NavigateChange(-1); view.NavigateChange(-1);
        check(view.ChangePosition.Index == 0, "Previous stops at the first change");
        var addition = GitDiffPreview.FromPatch("@@ -0,0 +1,2 @@\n+one\n+two\n");
        view.ShowPreview(preview);
        view.ShowPreview(addition); Pump();
        check(view.ChangePosition == (0, 1) && table.FocusedRowHandle == 0 && !next.IsEnabled && !previous.IsEnabled,
            "A new file is one change and stale scrolling cannot move it");
        view.ShowPreview(GitDiffPreview.FromPatch("Binary files a/logo.png and b/logo.png differ\n")); Pump();
        check(view.ChangePosition == (-1, 0) && !next.IsEnabled && !previous.IsEnabled, "Binary files have no text change targets");
        var gaps = GitDiffPreview.FromPatch("@@ -5 +5 @@\n-before\n+after\n@@ -80 +80 @@\n-last\n+latest\n");
        check(gaps.ChangeStarts.Count == 2, "Separate hunks are not merged across omitted context");
        view.ShowPreview(new([], Loc.Text("NoUncommittedChanges"))); Pump();
        check(view.ChangePosition == (-1, 0) && ((TextBlock)view.FindName("Notice")).Text == Loc.Text("NoUncommittedChanges"), "Clean repositories show a clear empty state");
        window.Close();
        string sessionFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DevWorkspace-diff-setting-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            System.IO.File.WriteAllText(sessionFile, "{\"Language\":\"en\"}");
            var session = SessionState.LoadFrom(sessionFile);
            check(session.OpenDiffOnDoubleClick, "Existing sessions default to opening diffs on double-click");
            session.OpenDiffOnDoubleClick = false;
            check(session.SaveTo(sessionFile) && !SessionState.LoadFrom(sessionFile).OpenDiffOnDoubleClick, "Project-open preference survives restart");
            var settings = new SettingsWindow("en", false);
            check(!settings.OpenDiffOnDoubleClick, "Settings reflects the saved project-open preference");
            settings.Close();
        }
        finally { System.IO.File.Delete(sessionFile); }

    }
}
