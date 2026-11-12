using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DevWorkspace;

public partial class MainWindow
{
    private readonly DispatcherTimer _findingTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    private void InitializeFindingReminders()
    {
        _findingTimer.Tick += (_, _) => UpdateTrackedFindings();
        _findingTimer.Start();
    }

    private void FindingContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: DashboardFinding finding, ContextMenu: { } menu })
            return;
        menu.Items.Clear();
        AddFindingMenuItems(menu, NewTrackedFinding(finding.Project, finding.Identity,
            $"{finding.Package} {finding.Version}", finding.Detail, "nuget-report"));
    }

    private void TrackGitFinding(ProjectEntry project)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        AddFindingMenuItems(menu, NewTrackedFinding(project, project.Git.State.ToString(),
            project.Name, $"{project.Branch} · {project.Git.Label}", "git-workbench"));
        menu.IsOpen = true;
    }

    private static TrackedFinding NewTrackedFinding(ProjectEntry project, string identity, string title, string detail, string target) =>
        new(JsonSerializer.Serialize(new[] { project.RelativePath, target, identity }), project.RelativePath, title, detail, target, DateTimeOffset.Now);

    private void AddFindingMenuItems(ContextMenu menu, TrackedFinding finding)
    {
        if (_workspace is not { } workspace)
            return;
        void Add(string label, Func<DateTimeOffset> due)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                var existing = _session.For(workspace.Root).TrackedFindings ?? [];
                SaveTrackedFindings(workspace.Root,
                    [.. existing.Where(previous => previous.Id != finding.Id), finding with { Due = due() }]);
                StatusText.Text = $"{finding.Title} takibe alındı.";
            };
            menu.Items.Add(item);
        }
        Add("Takip et · Bugün", () => DateTimeOffset.Now);
        Add("Tekrar hatırlat · Yarın 09.00", () => new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(9)));
        Add("Takip et · Bu hafta", () =>
        {
            int days = ((int)DayOfWeek.Friday - (int)DateTime.Today.DayOfWeek + 7) % 7;
            return new DateTimeOffset(DateTime.Today.AddDays(days).AddHours(9));
        });
    }

    private void SaveTrackedFindings(string root, TrackedFinding[] findings)
    {
        _session.RememberFindings(root, findings);
        _session.Save();
        UpdateTrackedFindings();
    }

    private void UpdateTrackedFindings()
    {
        if (DashboardFollowUp is null)
            return;
        var rows = _workspace is { } workspace ? _session.For(workspace.Root).TrackedFindings ?? [] : [];
        var due = rows.Where(row => row.Due <= DateTimeOffset.Now).OrderBy(row => row.Due).Select(row =>
        {
            var project = _projects.FirstOrDefault(project => project.RelativePath.Equals(row.Project, StringComparison.OrdinalIgnoreCase));
            return new DashboardLink(row.Title, row.Detail, project is null ? "tracked-findings" : row.Target, project);
        }).ToArray();
        DashboardFollowUp.Visibility = due.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DashboardFollowUpRows.ItemsSource = due;
    }

    private void TrackedFindingsClick(object sender, RoutedEventArgs e) => ShowTrackedFindings();

    private void ShowTrackedFindings()
    {
        if (_workspace is not { } workspace)
            return;
        var window = new TrackedFindingsWindow(_session.For(workspace.Root).TrackedFindings ?? [],
            rows => SaveTrackedFindings(workspace.Root, rows)) { Owner = this };
        if (window.ShowDialog() == true && window.Chosen is { } finding &&
            _projects.FirstOrDefault(project => project.RelativePath.Equals(finding.Project, StringComparison.OrdinalIgnoreCase)) is { } project)
        {
            if (finding.Target == "nuget-report")
                ShowProjectTools(ProjectToolView.NuGet, project);
            else
                OpenGitWorkbench(project);
        }
    }
}
