using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class ProjectToolsWindow
{
    private bool _actionsLoaded, _composeLoaded, _composeBusy;
    private Uri? _actionsLink;
    private CancellationTokenSource? _actionsLogin;

    private void HealthNuGetClick(object sender, RoutedEventArgs e) => NuGetTab.IsSelected = true;

    private async void NuGetScanClick(object sender, RoutedEventArgs e)
    {
        if (_nuGetState is not { CanScan: true } state)
            return;
        try
        {
            if (await ToolSetupWindow.EnsureReadyAsync(this, DevelopmentTool.DotNet, _closing.Token))
                await state.ScanAsync(_project, _closing.Token);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (IsIntegrationError(ex)) { NuGetDetails.SetCurrentValue(TextBlock.TextProperty, IntegrationMessage(ex)); }
    }

    private Uri? SelectedAdvisory => NuGetGrid.CurrentItem is NuGetFinding finding &&
        Uri.TryCreate(finding.Advisory, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;

    private void NuGetSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (NuGetAdvisoryButton is not null)
            NuGetAdvisoryButton.IsEnabled = SelectedAdvisory is not null;
    }

    private void NuGetAdvisoryClick(object sender, RoutedEventArgs e) => OpenIntegrationLink(SelectedAdvisory, NuGetDetails);

    private async void ActionsRefreshClick(object sender, RoutedEventArgs e) => await LoadActionsAsync();

    private async Task LoadActionsAsync()
    {
        await RunActionsOperationAsync(Loc.Text("L95E31BAC5ABC"), ReadActionsResultAsync);
    }

    private async Task ReadActionsResultAsync()
    {
        var (run, hasWorkflow) = await ProjectIntegrations.GetActionsOverviewAsync(_project, _closing.Token);
        if (_closing.IsCancellationRequested)
            return;
        _actionsLoaded = true;
        _remoteWorkflowExists = hasWorkflow;
        ActionsStatus.Text = run is null
            ? hasWorkflow ? Loc.Text("L906E5C019BBF")
                : Loc.Text("L988CCF038458")
            : "";
        if (run is null)
            return;
        ActionsHeadline.Text = $"{run.Workflow} · {run.Status}";
        ActionsTitle.Text = run.Title;
        ActionsMetadata.Text = string.Join(" · ", new[] { run.Branch, run.Started?.ToLocalTime().ToString("g") }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        ActionsFailures.Text = run.FailureDetails;
        ActionsFailures.Visibility = run.FailureDetails.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _actionsLink = run.Link;
        ActionsOpenButton.IsEnabled = _actionsLink is not null;
        ActionsResult.Visibility = Visibility.Visible;
    }

    private async Task RunActionsOperationAsync(string message, Func<Task> operation)
    {
        if (!ActionsRefreshButton.IsEnabled || _closing.IsCancellationRequested)
            return;
        ActionsRefreshButton.IsEnabled = false;
        WorkflowCreateButton.IsEnabled = false;
        ActionsResult.Visibility = ActionsInstallButton.Visibility = ActionsGuideButton.Visibility =
            ActionsLoginButton.Visibility = Visibility.Collapsed;
        _actionsLink = null;
        _actionsLoaded = false;
        ActionsStatus.Text = message;
        try { await operation(); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (GitHubCliMissingException ex)
        {
            ActionsStatus.Text = ex.Message;
            ActionsInstallButton.Visibility = ActionsGuideButton.Visibility = Visibility.Visible;
        }
        catch (GitHubLoginRequiredException ex)
        {
            ActionsStatus.Text = ex.Message;
            ActionsLoginButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (IsIntegrationError(ex))
        {
            ActionsStatus.Text = IntegrationMessage(ex);
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
            {
                ActionsRefreshButton.IsEnabled = true;
                UpdateWorkflowPanel();
            }
        }
    }

    private async void ActionsInstallClick(object sender, RoutedEventArgs e)
    {
        new ToolSetupWindow(DevelopmentTool.GitHub) { Owner = this }.ShowDialog();
        if (DevelopmentTools.Find(DevelopmentTool.GitHub) is not null)
            await LoadActionsAsync();
    }

    private void ActionsGuideClick(object sender, RoutedEventArgs e) => OpenIntegrationLink(GitHubCli.InstallGuide, ActionsStatus);

    private async void ActionsLoginClick(object sender, RoutedEventArgs e) =>
        await RunActionsOperationAsync(Loc.Text("L332073CC27FA"), SignInAndRefreshAsync);

    private async Task SignInAndRefreshAsync()
    {
        using var login = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _actionsLogin = login;
        ActionsCancelLoginButton.Visibility = Visibility.Visible;
        var progress = new Progress<string>(code =>
        {
            if (_actionsLogin != login || login.IsCancellationRequested || ActionsDevicePanel.Visibility == Visibility.Visible)
                return;
            ActionsDeviceCode.Text = code;
            ActionsDevicePanel.Visibility = Visibility.Visible;
            ActionsStatus.Text = Loc.Text("L4BC25669BE54");
            OpenIntegrationLink(GitHubCli.DeviceLogin, ActionsStatus);
        });
        try
        {
            await GitHubCli.LoginAsync(progress, login.Token);
        }
        catch (OperationCanceledException) when (!_closing.IsCancellationRequested)
        {
            ActionsStatus.Text = Loc.Text("L69B68B59862C");
            ActionsLoginButton.Visibility = Visibility.Visible;
            return;
        }
        catch
        {
            if (!_closing.IsCancellationRequested)
                ActionsLoginButton.Visibility = Visibility.Visible;
            throw;
        }
        finally
        {
            _actionsLogin = null;
            ActionsCancelLoginButton.Visibility = ActionsDevicePanel.Visibility = Visibility.Collapsed;
            ActionsDeviceCode.Clear();
        }
        ActionsStatus.Text = Loc.Text("L5E1089B17787");
        await ReadActionsResultAsync();
    }

    private void ActionsCancelLoginClick(object sender, RoutedEventArgs e) => _actionsLogin?.Cancel();

    private void ActionsDevicePageClick(object sender, RoutedEventArgs e) => OpenIntegrationLink(GitHubCli.DeviceLogin, ActionsStatus);

    private void ActionsOpenClick(object sender, RoutedEventArgs e) => OpenIntegrationLink(_actionsLink, ActionsStatus);

    private void OpenIntegrationLink(Uri? uri, TextBlock status)
    {
        if (uri is null)
            return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception ex) { status.SetCurrentValue(TextBlock.TextProperty, ex.Message); }
    }

    private async void ComposeRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DevelopmentTools.Find(DevelopmentTool.Docker) is null)
            new ToolSetupWindow(DevelopmentTool.Docker) { Owner = this }.ShowDialog();
        await RefreshComposeAsync();
    }

    private Task RefreshComposeAsync() => RunComposeOperationAsync("Docker Compose servisleri okunuyor…", UpdateComposeServicesAsync);

    private async Task UpdateComposeServicesAsync()
    {
        string? selected = (ComposeGrid.CurrentItem as ComposeService)?.Name;
        _composeLoaded = false;
        try
        {
            var services = await ProjectIntegrations.ReadComposeAsync(_project, _closing.Token);
            if (_closing.IsCancellationRequested)
                return;
            ComposeGrid.ItemsSource = services;
            ComposeGrid.CurrentItem = services.FirstOrDefault(service => service.Name == selected) ?? services.FirstOrDefault();
            _composeLoaded = true;
            ComposeStatus.Text = services.Count == 0 ? Loc.Text("L86B1E7F67AEA") : $"{services.Count:N0} servis";
        }
        catch
        {
            if (!_closing.IsCancellationRequested)
            {
                ComposeGrid.ItemsSource = null;
                ComposeOutput.Clear();
            }
            throw;
        }
    }

    private async void ComposeStartClick(object sender, RoutedEventArgs e) => await ChangeComposeAsync(start: true);
    private async void ComposeStopClick(object sender, RoutedEventArgs e) => await ChangeComposeAsync(start: false);

    private Task ChangeComposeAsync(bool start)
    {
        if (ComposeGrid.CurrentItem is not ComposeService service)
            return Task.CompletedTask;
        return RunComposeOperationAsync($"{service.Name} {(start ? Loc.Text("L8A18DC0B54BD") : Loc.Text("L9F3588621747"))}…", async () =>
        {
            await ProjectIntegrations.ChangeComposeAsync(_project, service.Name, start, _closing.Token);
            await UpdateComposeServicesAsync();
            if (!_closing.IsCancellationRequested)
                ComposeStatus.Text = $"{service.Name} {(start ? Loc.Text("LC78F01307F90") : Loc.Text("L4377FFACE0E6"))}.";
        });
    }

    private async void ComposeLogsClick(object sender, RoutedEventArgs e)
    {
        if (ComposeGrid.CurrentItem is not ComposeService service)
            return;
        await RunComposeOperationAsync(Loc.Format("LF660F9BC9D15", service.Name), async () =>
        {
            ComposeOutput.Clear();
            string logs = await ProjectIntegrations.ReadComposeLogsAsync(_project, service.Name, _closing.Token);
            if (_closing.IsCancellationRequested)
                return;
            ComposeOutput.Text = string.IsNullOrWhiteSpace(logs) ? Loc.Text("L8115A7F514D5") : logs;
            ComposeOutput.ScrollToEnd();
            ComposeStatus.Text = Loc.Format("L07B4F6E16E71", service.Name);
        });
    }

    private async Task RunComposeOperationAsync(string status, Func<Task> operation)
    {
        if (_composeBusy || _closing.IsCancellationRequested)
            return;
        _composeBusy = true;
        UpdateComposeActions();
        ComposeStatus.Text = status;
        try { await operation(); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (IsIntegrationError(ex)) { ComposeStatus.Text = IntegrationMessage(ex); }
        finally
        {
            _composeBusy = false;
            if (!_closing.IsCancellationRequested)
                UpdateComposeActions();
        }
    }

    private void ComposeSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (ComposeOutput is null)
            return;
        ComposeOutput.Clear();
        if (!_composeBusy && ComposeGrid.CurrentItem is ComposeService service)
            ComposeStatus.Text = $"{service.Name} · {service.Status}";
        UpdateComposeActions();
    }

    private void UpdateComposeActions()
    {
        ComposeRefreshButton.IsEnabled = ComposeGrid.IsEnabled = !_composeBusy;
        bool canAct = !_composeBusy && ComposeGrid.CurrentItem is ComposeService;
        ComposeStartButton.IsEnabled = ComposeStopButton.IsEnabled = ComposeLogsButton.IsEnabled = canAct;
    }

    private static bool IsIntegrationError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or JsonException or InvalidOperationException;

    private static string IntegrationMessage(Exception ex) => ex is JsonException or InvalidOperationException
        ? Loc.Text("L21506299539B") : ex.Message;
}
