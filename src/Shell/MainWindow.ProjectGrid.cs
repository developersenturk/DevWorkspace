using System.Globalization;
using System.Windows.Data;
using DevExpress.Xpf.Grid;

namespace DevWorkspace;

public partial class MainWindow
{
    private ProjectEntry[] _visibleProjects = [];
    private readonly HashSet<string> _expandedProjectGroups = new(StringComparer.OrdinalIgnoreCase);
    private string? _projectRowsWorkspace;

    private bool RowsBelongToCurrentWorkspace => string.Equals(_projectRowsWorkspace, _workspace?.Root, StringComparison.OrdinalIgnoreCase);

    private void CaptureExpandedProjectGroups()
    {
        if (!RowsBelongToCurrentWorkspace || VersionColumn.GroupIndex < 0)
            return;

        foreach (var group in _visibleProjects.Select((project, index) => (project.ProjectGroupKey, Index: index))
            .GroupBy(row => row.ProjectGroupKey, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Skip(1).Any() && ProjectGroupHandle(group.First().Index) is int handle)
            {
                if (ProjectGrid.IsGroupRowExpanded(handle))
                    _expandedProjectGroups.Add(group.Key);
                else
                    _expandedProjectGroups.Remove(group.Key);
            }
        }
    }

    private int? ProjectGroupHandle(int index)
    {
        int parent = ProjectGrid.GetParentRowHandle(ProjectGrid.GetRowHandleByListIndex(index));
        return ProjectGrid.IsGroupRowHandle(parent) ? parent : null;
    }

    private void ApplyProjectRows(ProjectEntry[] projects, bool groupVersions)
    {
        var groupCounts = projects.GroupBy(project => project.ProjectGroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        bool grouped = groupVersions && groupCounts.Values.Any(count => count > 1);
        bool changed = !RowsBelongToCurrentWorkspace || !_visibleProjects.SequenceEqual(projects);
        bool groupingChanged = (VersionColumn.GroupIndex >= 0) != grouped;
        if (changed || groupingChanged)
        {
            CaptureExpandedProjectGroups();
            _visibleProjects = projects;
            ProjectGrid.Tag = grouped
                ? groupCounts.Where(group => group.Value > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            _projectRowsWorkspace = _workspace?.Root;
            ProjectGrid.BeginDataUpdate();
            try
            {
                if (changed)
                    ProjectGrid.ItemsSource = projects;
                if (groupingChanged)
                    VersionColumn.GroupIndex = grouped ? 0 : -1;
            }
            finally
            {
                ProjectGrid.EndDataUpdate();
            }
            if (grouped)
            {
                var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < projects.Length; i++)
                {
                    string key = projects[i].ProjectGroupKey;
                    if (!handled.Add(key) || ProjectGroupHandle(i) is not int handle)
                        continue;

                    if (groupCounts[key] == 1 || _expandedProjectGroups.Contains(key))
                        ProjectGrid.ExpandGroupRow(handle);
                    else if (ProjectGrid.IsGroupRowExpanded(handle))
                        ProjectGrid.CollapseGroupRow(handle);
                }
            }
        }

        if (ProjectGrid.SelectedItem is null && projects.Length > 0)
        {
            ProjectEntry? first = grouped ? projects.FirstOrDefault(project => groupCounts[project.ProjectGroupKey] == 1) : projects[0];
            if (first is not null)
                FocusProject(first);
        }
        RefreshProjectDrawer();
    }

    private void ProjectGroupDisplayText(object sender, CustomGroupDisplayTextEventArgs e)
    {
        const string prefix = "group:";
        if (ReferenceEquals(e.Column, VersionColumn) && e.Value is string key && key.StartsWith(prefix, StringComparison.Ordinal))
            e.DisplayText = _visibleProjects.FirstOrDefault(project => project.ProjectGroupKey == key)?.Preference.Group?.Trim()
                ?? key[prefix.Length..];
    }

    private void FocusProject(ProjectEntry project)
    {
        int index = Array.IndexOf(_visibleProjects, project);
        if (index < 0)
            return;

        int handle = ProjectGrid.GetRowHandleByListIndex(index);
        int parent = ProjectGrid.GetParentRowHandle(handle);
        if (ProjectGrid.IsGroupRowHandle(parent))
            ProjectGrid.ExpandGroupRow(parent);

        ProjectGrid.SelectedItem = _visibleProjects[index];
        ProjectTableView.FocusedRowHandle = handle;
        ProjectTableView.ScrollIntoView(handle);
    }
}

public sealed class ProjectGroupMembershipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] is string key && values[1] is IReadOnlySet<string> groups && groups.Contains(key);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
