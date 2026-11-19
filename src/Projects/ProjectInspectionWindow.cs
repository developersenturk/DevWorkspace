using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal sealed class ProjectInspectionWindow : ThemedWindow
{
    internal ProjectInspectionWindow(string title, Func<CancellationToken, Task<InspectionRow[]>> inspect)
    {
        Title = title;
        Width = 830;
        Height = 450;
        MinWidth = 520;
        MinHeight = 260;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new CancellationTokenSource();
        var status = new TextBlock { Text = Loc.Text("LF56748ED4DD5"), Margin = new(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        var grid = WorkbenchGrid.Create(Title);
        grid.Columns.Add(WorkbenchGrid.Column(Loc.Text("L8BD057388E28"), "Category", new(130)));
        grid.Columns.Add(WorkbenchGrid.Column(Loc.Text("LC488576B91C5"), "Name", new(170)));
        grid.Columns.Add(WorkbenchGrid.Column(Loc.Text("L3902002D5818"), "Detail", new(1, DevExpress.Xpf.Grid.GridColumnUnitType.Star)));
        var panel = new DockPanel { Margin = new(12) };
        DockPanel.SetDock(status, Dock.Top);
        panel.Children.Add(status);
        panel.Children.Add(grid);
        Content = panel;
        Closed += (_, _) => cancel.Cancel();
        Loaded += async (_, _) =>
        {
            try
            {
                var rows = await Task.Run(() => inspect(cancel.Token), cancel.Token);
                grid.ItemsSource = rows;
                status.Text = rows.Length == 0 ? Loc.Text("L662E7500C48B") : Loc.Format("L92128C8B15EB", rows.Length);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                status.Text = ex.Message;
            }
        };
    }
}
