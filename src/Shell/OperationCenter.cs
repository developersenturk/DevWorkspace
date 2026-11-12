using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal sealed class WorkspaceOperation(string name, string target, Action? retry) : INotifyPropertyChanged
{
    private readonly StringBuilder _output = new();
    private string _status = "Çalışıyor";
    internal Action? Retry { get; } = retry;
    internal Action? Cancel { get; set; }
    public string Name { get; } = name;
    public string Target { get; } = target;
    public DateTime Started { get; } = DateTime.Now;
    public string Status => _status;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal string ReadOutput()
    {
        lock (_output)
            return _output.ToString();
    }

    internal void Append(string message)
    {
        lock (_output)
        {
            _output.Append(message);
            if (_output.Length > 160_000)
                _output.Remove(0, _output.Length - 160_000);
        }
    }

    internal void Finish(string status, string? detail = null)
    {
        Cancel = null;
        if (detail is not null)
            Append(Environment.NewLine + detail);
        _status = status;
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }
}

internal static class OperationCenter
{
    internal static ObservableCollection<WorkspaceOperation> Entries { get; } = [];

    internal static WorkspaceOperation Start(string name, string target, Action? retry = null)
    {
        var operation = new WorkspaceOperation(name, target, retry);
        Entries.Insert(0, operation);
        if (Entries.Count > 150 && Entries.LastOrDefault(item => item.Status != "Çalışıyor") is { } oldest)
            Entries.Remove(oldest);
        return operation;
    }

    internal static async Task RunCommandAsync(ProjectCommand command, string target, string folder, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var operation = Start(command.Name, target, () => _ = RunCommandAsync(command, target, folder, token));
        operation.Cancel = cancellation.Cancel;
        operation.Append("> " + command.Display + Environment.NewLine);
        try
        {
            int code = await ProjectTools.RunAsync(command, folder, operation.Append, cancellation.Token);
            operation.Finish(code == 0 ? "Tamamlandı" : "Başarısız", $"Çıkış kodu {code}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            operation.Finish("Durduruldu");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            operation.Finish("Başarısız", ex.Message);
        }
    }
}

internal sealed class OperationCenterWindow : ThemedWindow
{
    internal OperationCenterWindow()
    {
        Title = "İşlem merkezi";
        Width = 860;
        Height = 570;
        MinWidth = 520;
        MinHeight = 330;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid
        {
            ItemsSource = OperationCenter.Entries, AutoGenerateColumns = false, IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single
        };
        void Column(string header, string property, DataGridLength width, string? format = null) =>
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property) { StringFormat = format }, Width = width });
        Column("İşlem", "Name", new(2, DataGridLengthUnitType.Star));
        Column("Hedef", "Target", new(2, DataGridLengthUnitType.Star));
        Column("Durum", "Status", new(110));
        Column("Başlangıç", "Started", new(135), "g");
        var output = new TextBox
        {
            IsReadOnly = true, FontFamily = new("Consolas"), TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        System.Windows.Automation.AutomationProperties.SetName(output, "İşlem çıktısı");
        var retry = new Button { Content = "Yeniden çalıştır", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var stop = new Button { Content = "Durdur", Margin = new(0, 8, 8, 0) };
        stop.Click += (_, _) => (grid.SelectedItem as WorkspaceOperation)?.Cancel?.Invoke();
        retry.Click += (_, _) =>
        {
            if (grid.SelectedItem is WorkspaceOperation { Status: "Başarısız", Retry: { } repeat })
            {
                repeat();
                grid.SelectedIndex = 0;
            }
        };
        void UpdateOutput()
        {
            var selected = grid.SelectedItem as WorkspaceOperation;
            string text = selected?.ReadOutput() ?? "";
            if (output.Text != text)
                output.Text = text;
            retry.IsEnabled = selected is { Status: "Başarısız", Retry: not null };
            stop.IsEnabled = selected is { Status: "Çalışıyor", Cancel: not null };
        }
        grid.SelectionChanged += (_, _) => UpdateOutput();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => UpdateOutput();
        Loaded += (_, _) => { grid.SelectedIndex = 0; timer.Start(); };
        Closed += (_, _) => timer.Stop();

        var panel = new Grid { Margin = new(12) };
        panel.RowDefinitions.Add(new() { Height = new(2, GridUnitType.Star) });
        panel.RowDefinitions.Add(new() { Height = new(8) });
        panel.RowDefinitions.Add(new() { Height = new(3, GridUnitType.Star) });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(output, 2);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(stop);
        buttons.Children.Add(retry);
        Grid.SetRow(buttons, 3);
        panel.Children.Add(grid);
        panel.Children.Add(output);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
