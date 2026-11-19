using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace DevWorkspace;

public partial class MainWindow
{
    internal Task ShowWelcomeIfNeededAsync()
    {
        UsageMetrics.Record(_session, "app_open");
        if (!_session.WelcomeCompleted && _session.LastWorkspace is null) ShowWelcome();
        return Task.CompletedTask;
    }

    private void WelcomeClick(object sender, RoutedEventArgs e) => ShowWelcome();

    private void ShowWelcome()
    {
        if (_busy) return;
        var wizard = new WelcomeWindow(async root =>
        {
            await OpenWorkspaceAsync(root);
            if (_workspace?.Root.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) != true) return null;
            if (_navigationNodes.TryGetValue("overview", out var overview)) Navigation.SelectedItem = overview;
            return _projects.Count;
        }) { Owner = this };
        wizard.ShowDialog();
        _session.WelcomeCompleted = true;
        _session.Save();
    }

    private void ReportWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _workspace is null) return;
        var choice = MessageBox.Show(this, Loc.Text("ReportNamePrompt"), Loc.Text("ReportExport"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice == MessageBoxResult.Cancel) return;
        var picker = new SaveFileDialog { FileName = "DevWorkspace-report.html", Filter = "HTML (*.html)|*.html", DefaultExt = ".html" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            WorkspaceSharing.WriteAtomic(picker.FileName, WorkspaceSharing.Report(_projects, choice == MessageBoxResult.Yes));
            UsageMetrics.Record(_session, "report_export");
            StatusText.Text = Loc.Text("GrowthSaved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowGrowthError(ex); }
    }

    private void BugReportClick(object sender, RoutedEventArgs e)
    {
        // Only deliberately selected, non-identifying fields are placed in the URL.
        string body = $"### What happened?\n\n### Steps to reproduce\n1. \n\n### Expected behavior\n\n### Environment\nDevWorkspace {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\nWindows {Environment.OSVersion.Version}\nLanguage: {Loc.Language}\n";
        string url = "https://github.com/developersenturk/DevWorkspace/issues/new?template=bug_report.md&body=" + Uri.EscapeDataString(body);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); UsageMetrics.Record(_session, "bug_report"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { ShowGrowthError(ex); }
    }

    private void UsageClick(object sender, RoutedEventArgs e)
    {
        var window = new DevExpress.Xpf.Core.ThemedWindow
        {
            Owner = this, Title = Loc.Text("UsageTitle"), Width = 530, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var body = new System.Windows.Controls.StackPanel { Margin = new(24) };
        body.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.Text("UsagePrivacy"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) });
        var enabled = new System.Windows.Controls.CheckBox { Content = Loc.Text("UsageEnable"), IsChecked = _session.UsageMetricsEnabled };
        body.Children.Add(enabled);
        var summary = new System.Windows.Controls.TextBox { Text = JsonSerializer.Serialize(_session.UsageCounters, new JsonSerializerOptions { WriteIndented = true }),
            IsReadOnly = true, MaxHeight = 210, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Margin = new(0, 16, 0, 16) };
        body.Children.Add(summary);
        var clear = new System.Windows.Controls.Button { Content = Loc.Text("UsageClear"), Padding = new(10), Margin = new(0, 0, 0, 10) };
        clear.Click += (_, _) =>
        {
            var previous = _session.UsageCounters;
            _session.UsageCounters = new();
            if (!_session.SaveTo(SessionState.FilePath)) { _session.UsageCounters = previous; ShowGrowthError(new IOException(Loc.Text("SettingsSaveFailed"))); return; }
            summary.Text = "{}";
        };
        body.Children.Add(clear);
        var export = new System.Windows.Controls.Button { Content = Loc.Text("UsageExport"), Padding = new(10), Margin = new(0, 0, 0, 10) };
        export.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { FileName = "DevWorkspace-usage.json", Filter = "JSON (*.json)|*.json" };
            if (picker.ShowDialog(window) != true) return;
            try { WorkspaceSharing.WriteAtomic(picker.FileName, summary.Text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowGrowthError(ex); }
        };
        body.Children.Add(export);
        var save = new System.Windows.Controls.Button { Content = Loc.Text("UsageSave"), Padding = new(10), IsDefault = true };
        save.Click += (_, _) =>
        {
            bool previous = _session.UsageMetricsEnabled;
            _session.UsageMetricsEnabled = enabled.IsChecked == true;
            if (!_session.SaveTo(SessionState.FilePath)) { _session.UsageMetricsEnabled = previous; ShowGrowthError(new IOException(Loc.Text("SettingsSaveFailed"))); return; }
            window.Close();
        };
        body.Children.Add(save); window.Content = body; window.ShowDialog();
    }

    private void ShowGrowthError(Exception error) => MessageBox.Show(this, error.Message, "DevWorkspace", MessageBoxButton.OK, MessageBoxImage.Warning);
}
