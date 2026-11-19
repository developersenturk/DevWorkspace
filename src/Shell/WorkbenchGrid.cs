using System.Collections;
using System.Windows.Automation;
using DevExpress.Xpf.Editors.Settings;
using DevExpress.Xpf.Grid;

namespace DevWorkspace;

// Tables created in code use the same DevExpress view as the XAML screens.
internal static class WorkbenchGrid
{
    internal static GridControl Create(string accessibleName)
    {
        var grid = new GridControl
        {
            AutoGenerateColumns = AutoGenerateColumnsMode.None,
            View = new TableView
            {
                AutoWidth = false, AllowEditing = false, NavigationStyle = GridViewNavigationStyle.Row,
                ShowGroupPanel = false, ShowIndicator = false, ShowEmptyText = true, RowMinHeight = 28
            }
        };
        AutomationProperties.SetName(grid, accessibleName);
        return grid;
    }

    internal static GridColumn Column(string header, string field, GridColumnWidth width, string? format = null) => new()
    {
        Header = header, FieldName = field, Width = width, MinWidth = width.IsStar ? 160 : width.Value, ReadOnly = true,
        EditSettings = new TextEditSettings { DisplayFormat = format }
    };

    internal static void SelectFirst(GridControl grid) => grid.CurrentItem = grid.GetRow(0)
        ?? (grid.ItemsSource as IEnumerable)?.Cast<object>().FirstOrDefault();
}
