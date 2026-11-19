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
    private string _status = Loc.Text("LAF7DA0BB4DFC");
    internal Action? Retry { get; } = retry;
    internal Action? Cancel { get; set; }
    public string Name { get; } = name;
    public string Target { get; } = target;
    public DateTime Started { get; } = DateTime.Now;
    public string Status => _status;
    internal bool IsRunning => _status == Loc.Text("LAF7DA0BB4DFC");
    internal bool IsFailed => _status == Loc.Text("LF0873175A881");
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
        if (Entries.Count > 150 && Entries.LastOrDefault(item => item.Status != Loc.Text("LAF7DA0BB4DFC")) is { } oldest)
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
            operation.Finish(code == 0 ? Loc.Text("L7C6F3F5F37BC") : Loc.Text("LF0873175A881"), Loc.Format("L36D2AD5696B2", code));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            operation.Finish(Loc.Text("L7C925A8624F1"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            operation.Finish(Loc.Text("LF0873175A881"), ex.Message);
        }
    }
}

internal sealed class OperationCenterWindow : ThemedWindow
{
    internal OperationCenterWindow()
    {
        Title = Loc.Text("L4328F6541368");
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
        Column(Loc.Text("LB93C839F51F3"), "Name", new(2, DataGridLengthUnitType.Star));
        Column(Loc.Text("LA3973F7CD7C0"), "Target", new(2, DataGridLengthUnitType.Star));
        Column(Loc.Text("L8BD057388E28"), "Status", new(110));
        Column(Loc.Text("L296CCC7AC21E"), "Started", new(135), "g");
        var output = new TextBox
        {
            IsReadOnly = true, FontFamily = new("Consolas"), TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        System.Windows.Automation.AutomationProperties.SetName(output, Loc.Text("L352303F6BCA1"));
        var retry = new Button { Content = Loc.Text("L2089D743B42A"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 8, 0, 0) };
        var stop = new Button { Content = Loc.Text("L5769A8F8AECD"), Margin = new(0, 8, 8, 0) };
        stop.Click += (_, _) => (grid.SelectedItem as WorkspaceOperation)?.Cancel?.Invoke();
        retry.Click += (_, _) =>
        {
            if (grid.SelectedItem is WorkspaceOperation { IsFailed: true, Retry: { } repeat })
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
            retry.IsEnabled = selected is { IsFailed: true, Retry: not null };
            stop.IsEnabled = selected is { IsRunning: true, Cancel: not null };
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
