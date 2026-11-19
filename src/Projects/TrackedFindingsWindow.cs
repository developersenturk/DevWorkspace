using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal sealed class TrackedFindingsWindow : ThemedWindow
{
    internal TrackedFinding? Chosen { get; private set; }

    internal TrackedFindingsWindow(TrackedFinding[] findings, Action<TrackedFinding[]> save)
    {
        Title = Loc.Text("L333EC4379D57");
        Width = 810;
        Height = 425;
        MinWidth = 530;
        MinHeight = 280;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.Text("L293390C82A9F"), Binding = new Binding("Title"), Width = new(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.Text("L3902002D5818"), Binding = new Binding("Detail"), Width = new(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.Text("L04AA9BDF5F2E"), Binding = new Binding("Due.LocalDateTime") { StringFormat = "g" }, Width = new(145) });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        void AddButton(string label, Action<TrackedFinding> action)
        {
            var button = new Button { Content = label, Padding = new(8, 4, 8, 4), Margin = new(8, 0, 0, 0) };
            button.Click += (_, _) =>
            {
                if (grid.SelectedItem is TrackedFinding item)
                    action(item);
            };
            footer.Children.Add(button);
        }
        void ShowRows()
        {
            grid.ItemsSource = findings.OrderBy(row => row.Due).ToArray();
            grid.SelectedIndex = 0;
        }
        AddButton(Loc.Text("LF269BAD26538"), item =>
        {
            findings = findings.Select(row => row.Id == item.Id
                ? row with { Due = new(DateTime.Today.AddDays(1).AddHours(9)) } : row).ToArray();
            save(findings);
            ShowRows();
        });
        AddButton(Loc.Text("L0D5D47845817"), item =>
        {
            findings = findings.Where(row => row.Id != item.Id).ToArray();
            save(findings);
            ShowRows();
        });
        AddButton(Loc.Text("LA48D8CC135CC"), item => { Chosen = item; DialogResult = true; });
        var panel = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        panel.Children.Add(grid);
        Content = panel;
        ShowRows();
    }
}
