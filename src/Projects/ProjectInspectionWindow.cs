using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        var status = new TextBlock { Text = "İnceleniyor…", Margin = new(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "Durum", Binding = new Binding("Category"), Width = new(130) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Öğe", Binding = new Binding("Name"), Width = new(170) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ayrıntı", Binding = new Binding("Detail"), Width = new(1, DataGridLengthUnitType.Star) });
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
                status.Text = rows.Length == 0 ? "Gösterilecek sonuç bulunamadı." : $"{rows.Length:N0} sonuç";
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                status.Text = ex.Message;
            }
        };
    }
}
