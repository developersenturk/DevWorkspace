using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Grid;

namespace DevWorkspace;

internal sealed class WorkspaceOperation(string name, string target, Action? retry) : INotifyPropertyChanged
{
    private readonly StringBuilder _output = new();
    private string _status = Loc.Text("LAF7DA0BB4DFC");
    private bool _running = true;
    private bool _failed;
    internal Action? Retry { get; } = retry;
    internal Action? Cancel { get; set; }
    internal ProjectCommand? Command { get; set; }
    internal string? Folder { get; set; }
    internal string? EnvironmentFile { get; set; }
    public int? ProcessId { get; internal set; }
    internal DateTime? ProcessStartedUtc { get; set; }
    public string Ports { get; internal set; } = "—";
    public string Name { get; } = name;
    public string Target { get; } = target;
    public DateTime Started { get; } = DateTime.Now;
    public string Status => _status;
    internal bool IsRunning => _running;
    internal bool IsFailed => _failed;
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
        _running = false;
        _failed = status == Loc.Text("LF0873175A881");
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
        if (Entries.Count > 150 && Entries.LastOrDefault(item => !item.IsRunning) is { } oldest)
            Entries.Remove(oldest);
        return operation;
    }

    internal static async Task RunCommandAsync(ProjectCommand command, string target, string folder, CancellationToken token, string? environmentFile = null)
    {
        if (Entries.Any(e => e.IsRunning && e.Command == command && string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase))) return;
        await StartCommand(command, target, folder, token, environmentFile).Completion;
    }

    internal static (WorkspaceOperation Operation, Task Completion) StartCommand(ProjectCommand command, string target, string folder, CancellationToken token, string? environmentFile = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (ArtifactCleanup.IsCleaning(folder)) throw new IOException(WorkbenchText.T("CleanupRunning"));
        var operation = Start(command.Name, target, () => _ = RunCommandAsync(command, target, folder, token, environmentFile));
        operation.EnvironmentFile = environmentFile;
        return (operation, ExecuteAsync(operation, command, folder, token, environment));
    }

    private static async Task ExecuteAsync(WorkspaceOperation operation, ProjectCommand command, string folder, CancellationToken token,
        IReadOnlyDictionary<string, string>? environment)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        operation.Command = command;
        operation.Folder = folder;
        operation.Cancel = cancellation.Cancel;
        operation.Append("> " + command.Display + Environment.NewLine);
        try
        {
            environment ??= operation.EnvironmentFile is { } file ? await Task.Run(() => LaunchEnvironment.Read(file), cancellation.Token) : null;
            int code = await ProjectTools.RunAsync(command, folder, operation.Append, cancellation.Token, process =>
            {
                operation.ProcessId = process.Id;
                operation.ProcessStartedUtc = process.StartTime.ToUniversalTime();
            }, environment);
            operation.Finish(code == 0 ? Loc.Text("L7C6F3F5F37BC") : Loc.Text("LF0873175A881"), Loc.Format("L36D2AD5696B2", code));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            operation.Finish(Loc.Text("L7C925A8624F1"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException)
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
        var grid = WorkbenchGrid.Create(Title);
        grid.ItemsSource = OperationCenter.Entries;
        void Column(string header, string property, GridColumnWidth width, string? format = null) =>
            grid.Columns.Add(WorkbenchGrid.Column(header, property, width, format));
        Column(Loc.Text("LB93C839F51F3"), "Name", new(2, GridColumnUnitType.Star));
        Column(Loc.Text("LA3973F7CD7C0"), "Target", new(2, GridColumnUnitType.Star));
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
        stop.Click += (_, _) => (grid.CurrentItem as WorkspaceOperation)?.Cancel?.Invoke();
        retry.Click += (_, _) =>
        {
            if (grid.CurrentItem is WorkspaceOperation { IsFailed: true, Retry: { } repeat })
            {
                repeat();
                WorkbenchGrid.SelectFirst(grid);
            }
        };
        void UpdateOutput()
        {
            var selected = grid.CurrentItem as WorkspaceOperation;
            string text = selected?.ReadOutput() ?? "";
            if (output.Text != text)
                output.Text = text;
            retry.IsEnabled = selected is { IsFailed: true, Retry: not null };
            stop.IsEnabled = selected is { IsRunning: true, Cancel: not null };
        }
        grid.CurrentItemChanged += (_, _) => UpdateOutput();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => UpdateOutput();
        Loaded += (_, _) => { WorkbenchGrid.SelectFirst(grid); UpdateOutput(); timer.Start(); };
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
