using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal enum ProjectToolView { Commands, Health, NuGet, Actions, Compose }

public partial class ProjectToolsWindow : ThemedWindow
{
    private sealed record Option(ProjectCommand Command, bool Saved)
    {
        public string Label => (Saved ? Loc.Text("LDA1426F3A8FF") : Loc.Text("LC1BE4BCA8639")) + Command.Name;
    }

    private Workspace _workspace;
    private readonly ProjectEntry _project;
    private readonly NuGetScanState? _nuGetState;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _outputTimer;
    private readonly StringBuilder _pending = new();
    private IReadOnlyList<ProjectCommand> _presets = [];
    private CancellationTokenSource? _running;
    private bool _checking, _checked, _loading, _saving;
    internal Workspace? UpdatedWorkspace { get; private set; }

    internal ProjectToolsWindow(Workspace workspace, ProjectEntry project, ProjectToolView view, NuGetScanState? nuGetState,
        HealthResult? healthReport = null)
    {
        InitializeComponent();
        _workspace = workspace;
        _project = project;
        _nuGetState = nuGetState;
        _outputTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(125) };
        _outputTimer.Tick += (_, _) => FlushOutput();
        ProjectNameText.Text = project.Name;
        ProjectFolderText.Text = project.Folder;
        TerminalButton.IsEnabled = Directory.Exists(project.Folder);
        NuGetTab.DataContext = _nuGetState;
        NuGetTab.Visibility = _nuGetState is null ? Visibility.Collapsed : Visibility.Visible;
        HealthNuGetButton.Visibility = NuGetTab.Visibility;
        ActionsTab.Visibility = project.Repository is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateWorkflowPanel();
        ComposeTab.Visibility = ProjectIntegrations.HasCompose(project) ? Visibility.Visible : Visibility.Collapsed;
        if (healthReport is not null)
            ShowHealthReport(healthReport);
        ToolTabs.SelectedItem = view switch
        {
            ProjectToolView.Health => HealthTab,
            ProjectToolView.NuGet => NuGetTab,
            ProjectToolView.Actions => ActionsTab,
            ProjectToolView.Compose => ComposeTab,
            _ => ToolTabs.Items[0]
        };
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _presets = await Task.Run(() => ProjectTools.Presets(_project), _closing.Token);
            if (!_closing.IsCancellationRequested)
                LoadOptions();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        finally { _loading = false; }

        await LoadSelectedToolAsync();
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _outputTimer.Stop();
        _closing.Cancel();
    }

    private void LoadOptions(string? selected = null)
    {
        var saved = _workspace.PreferenceFor(_project.RelativePath).Commands ?? [];
        var items = _presets.Select(command => new Option(command, false))
            .Concat(saved.Where(command => command is not null && !string.IsNullOrWhiteSpace(command.Executable))
                .Select(command => new Option(command, true))).ToArray();
        CommandPicker.ItemsSource = items;
        CommandPicker.SelectedItem = items.FirstOrDefault(item => item.Saved &&
            item.Command.Name.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
        UpdateCommandActions();
    }

    private void CommandChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunButton is null || CommandPreview is null)
            return;
        var option = CommandPicker.SelectedItem as Option;
        CommandPreview.Text = option?.Command.Display ?? Loc.Text("LD942EA74D8E2");
        CommandName.Text = option?.Saved == true ? option.Command.Name : "";
        CommandExecutable.Text = option?.Saved == true ? option.Command.Executable : "";
        CommandArguments.Text = option?.Saved == true ? option.Command.Arguments : "";
        UpdateCommandActions();
    }

    private void UpdateCommandActions()
    {
        RunButton.IsEnabled = _running is null && CommandPicker.SelectedItem is Option && Directory.Exists(_project.Folder);
        StopButton.IsEnabled = _running is not null;
        RemoveCommandButton.IsEnabled = !_saving && CommandPicker.SelectedItem is Option { Saved: true };
    }

    private async void SaveCommandClick(object sender, RoutedEventArgs e)
    {
        string name = CommandName.Text.Trim();
        string executable = CommandExecutable.Text.Trim().Trim('"');
        string arguments = CommandArguments.Text.Trim();
        if (name.Length == 0 || executable.Length == 0 || executable.Any(char.IsControl) || arguments.Any(ch => ch is '\r' or '\n'))
        {
            CommandStatus.Text = Loc.Text("L8F42D08C09B3");
            return;
        }
        await SaveAsync(commands => commands.Where(command => !command.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Append(new(name, executable, arguments)).ToArray(), name);
    }

    private async void RemoveCommandClick(object sender, RoutedEventArgs e)
    {
        if (CommandPicker.SelectedItem is Option { Saved: true } option)
            await SaveAsync(commands => commands.Where(command => !command.Name.Equals(option.Command.Name, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private async Task SaveAsync(Func<ProjectCommand[], ProjectCommand[]> change, string? selected = null)
    {
        if (_saving)
            return;
        _saving = true;
        UpdateCommandActions();
        try
        {
            var updated = await Task.Run(() => _workspace.SavePreference(_project.RelativePath,
                preference => preference with { Commands = change(preference.Commands ?? []) }));
            _workspace = UpdatedWorkspace = updated;
            if (!_closing.IsCancellationRequested)
            {
                LoadOptions(selected);
                CommandStatus.Text = Loc.Text("LA8DCC5911FD8");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (!_closing.IsCancellationRequested)
                CommandStatus.Text = ex.Message;
        }
        finally
        {
            _saving = false;
            if (!_closing.IsCancellationRequested)
                UpdateCommandActions();
        }
    }

    private void TerminalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo") { UseShellExecute = true, WorkingDirectory = _project.Folder });
        }
        catch (System.ComponentModel.Win32Exception ex) { CommandStatus.Text = ex.Message; }
    }

    private async void RunClick(object sender, RoutedEventArgs e)
    {
        if (_running is not null || CommandPicker.SelectedItem is not Option option)
            return;

        using var running = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _running = running;
        CommandOutput.Clear();
        CommandOutput.AppendText("> " + option.Command.Display + Environment.NewLine);
        CommandStatus.Text = Loc.Text("LAEC20C3BB94E");
        UpdateCommandActions();
        _outputTimer.Start();
        try
        {
            if (ProjectTools.RequiredTool(option.Command) is { } tool)
            {
                if (tool == DevelopmentTool.Flutter && !await EnsureToolAsync(DevelopmentTool.Git, running.Token))
                    return;
                if (!await EnsureToolAsync(tool, running.Token))
                    return;
                CommandStatus.Text = Loc.Text("LAEC20C3BB94E");
            }
            var operation = OperationCenter.Start(option.Command.Name, _project.Name,
                () => _ = OperationCenter.RunCommandAsync(option.Command, _project.Name, _project.Folder,
                    (Owner as MainWindow)?.WorkbenchCancellation ?? CancellationToken.None));
            operation.Append("> " + option.Command.Display + Environment.NewLine);
            int exitCode;
            try
            {
                exitCode = await ProjectTools.RunAsync(option.Command, _project.Folder, text =>
                {
                    QueueOutput(text);
                    operation.Append(text);
                }, running.Token);
                operation.Finish(exitCode == 0 ? Loc.Text("L7C6F3F5F37BC") : Loc.Text("LF0873175A881"), Loc.Format("L36D2AD5696B2", exitCode));
            }
            catch (OperationCanceledException)
            {
                operation.Finish(Loc.Text("L7C925A8624F1"));
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
            {
                operation.Finish(Loc.Text("LF0873175A881"), ex.Message);
                throw;
            }
            if (!_closing.IsCancellationRequested)
                CommandStatus.Text = exitCode == 0 ? Loc.Text("L34A85FCC5C23") : Loc.Format("L3289E86BF3AB", exitCode);
        }
        catch (OperationCanceledException)
        {
            if (!_closing.IsCancellationRequested) CommandStatus.Text = Loc.Text("LC6EDBFBEA99C");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (!_closing.IsCancellationRequested) CommandStatus.Text = ex.Message;
        }
        finally
        {
            _running = null;
            _outputTimer.Stop();
            if (!_closing.IsCancellationRequested)
            {
                FlushOutput();
                UpdateCommandActions();
            }
        }
    }

    private void DevelopmentToolsClick(object sender, RoutedEventArgs e) =>
        new ToolSetupWindow { Owner = this }.ShowDialog();

    private async Task<bool> EnsureToolAsync(DevelopmentTool tool, CancellationToken token)
    {
        if (await ToolSetupWindow.EnsureReadyAsync(this, tool, token))
            return true;
        CommandStatus.Text = Loc.Format("LFDD86D088E9E", DevelopmentTools.Get(tool).Name);
        return false;
    }

    private void StopClick(object sender, RoutedEventArgs e) => _running?.Cancel();

    private void QueueOutput(string text)
    {
        lock (_pending)
        {
            if (_pending.Length + text.Length > 140_000)
                _pending.Remove(0, _pending.Length + text.Length - 140_000);
            _pending.Append(text);
        }
    }

    private void FlushOutput()
    {
        string text;
        lock (_pending)
        {
            if (_pending.Length == 0)
                return;
            text = _pending.ToString();
            _pending.Clear();
        }
        if (CommandOutput.Text.Length + text.Length > 180_000)
            CommandOutput.Text = CommandOutput.Text[^Math.Min(CommandOutput.Text.Length, 35_000)..];
        CommandOutput.AppendText(text);
        CommandOutput.ScrollToEnd();
    }

    private async void ToolTabChanged(object sender, TabControlSelectionChangedEventArgs e)
    {
        if (IsLoaded && !_loading)
            await LoadSelectedToolAsync();
    }

    private Task LoadSelectedToolAsync()
    {
        if (HealthTab.IsSelected && !_checked)
            return CheckHealthAsync();
        if (ActionsTab.IsSelected && !_actionsLoaded)
            return LoadActionsAsync();
        if (ComposeTab.IsSelected && !_composeLoaded)
            return RefreshComposeAsync();
        return Task.CompletedTask;
    }

    private async void HealthRefreshClick(object sender, RoutedEventArgs e) => await CheckHealthAsync();

    private async Task CheckHealthAsync()
    {
        if (_checking || _closing.IsCancellationRequested)
            return;
        _checking = true;
        HealthRefresh.IsEnabled = false;
        HealthSummary.Text = Loc.Text("L0E67B340C1AC");
        try
        {
            var result = await Task.Run(() => ProjectHealth.CheckAsync(_workspace, _project, _closing.Token), _closing.Token);
            if (_closing.IsCancellationRequested)
                return;
            ShowHealthReport(result);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            HealthSummary.Text = ex.Message;
        }
        finally
        {
            _checking = false;
            if (!_closing.IsCancellationRequested) HealthRefresh.IsEnabled = true;
        }
    }

    private void ShowHealthReport(HealthResult result)
    {
        HealthGrid.ItemsSource = result.Findings;
        _checked = true;
        HealthSummary.Text = Loc.Format("LACA7EA77904F", result.FilesChecked, result.Findings.Count) +
            (result.Unresolved > 0 ? Loc.Format("L3951FA21B42A", result.Unresolved) : "") +
            (result.Skipped > 0 ? Loc.Format("LFEBF14235DFE", result.Skipped) : "");
    }

    private void HealthSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (OpenHealthFileButton is not null)
            OpenHealthFileButton.IsEnabled = HealthGrid.CurrentItem is HealthFinding { File: string file } && File.Exists(file);
    }

    private void OpenHealthFileClick(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.CurrentItem is not HealthFinding { File: string file } || !File.Exists(file))
            return;
        try
        {
            var info = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            info.ArgumentList.Add(file);
            Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex) { HealthSummary.Text = ex.Message; }
    }
}
