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
        if (_nuGetState is { } state)
            await state.ScanAsync(_project, _closing.Token);
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
        await RunActionsOperationAsync("GitHub Actions sonucu alınıyor…", ReadActionsResultAsync);
    }

    private async Task ReadActionsResultAsync()
    {
        var run = await ProjectIntegrations.GetLatestRunAsync(_project, _closing.Token);
        if (_closing.IsCancellationRequested)
            return;
        _actionsLoaded = true;
        ActionsStatus.Text = run is null ? "Bu depoda iş akışı çalışması bulunamadı." : "";
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

    private async Task RunActionsOperationAsync(string message, Func<Task> operation, bool retryInstall = false)
    {
        if (!ActionsRefreshButton.IsEnabled || _closing.IsCancellationRequested)
            return;
        ActionsRefreshButton.IsEnabled = false;
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
            if (retryInstall)
                ActionsInstallButton.Visibility = ActionsGuideButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
                ActionsRefreshButton.IsEnabled = true;
        }
    }

    private async void ActionsInstallClick(object sender, RoutedEventArgs e) =>
        await RunActionsOperationAsync("GitHub CLI, Windows Paket Yöneticisi ile kuruluyor…", async () =>
        {
            await GitHubCli.InstallAsync(_closing.Token);
            await ReadActionsResultAsync();
        }, retryInstall: true);

    private void ActionsGuideClick(object sender, RoutedEventArgs e) => OpenIntegrationLink(GitHubCli.InstallGuide, ActionsStatus);

    private async void ActionsLoginClick(object sender, RoutedEventArgs e) =>
        await RunActionsOperationAsync("GitHub giriş kodu alınıyor…", SignInAndRefreshAsync);

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
            ActionsStatus.Text = "Tarayıcıda GitHub onayı bekleniyor…";
            OpenIntegrationLink(GitHubCli.DeviceLogin, ActionsStatus);
        });
        try
        {
            await GitHubCli.LoginAsync(progress, login.Token);
        }
        catch (OperationCanceledException) when (!_closing.IsCancellationRequested)
        {
            ActionsStatus.Text = "GitHub girişi iptal edildi.";
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
        ActionsStatus.Text = "GitHub girişi tamamlandı. Actions sonucu alınıyor…";
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

    private async void ComposeRefreshClick(object sender, RoutedEventArgs e) => await RefreshComposeAsync();

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
            ComposeStatus.Text = services.Count == 0 ? "Etkin bir Compose servisi bulunamadı." : $"{services.Count:N0} servis";
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
        return RunComposeOperationAsync($"{service.Name} {(start ? "başlatılıyor" : "durduruluyor")}…", async () =>
        {
            await ProjectIntegrations.ChangeComposeAsync(_project, service.Name, start, _closing.Token);
            await UpdateComposeServicesAsync();
            if (!_closing.IsCancellationRequested)
                ComposeStatus.Text = $"{service.Name} {(start ? "başlatıldı" : "durduruldu")}.";
        });
    }

    private async void ComposeLogsClick(object sender, RoutedEventArgs e)
    {
        if (ComposeGrid.CurrentItem is not ComposeService service)
            return;
        await RunComposeOperationAsync($"{service.Name} logları okunuyor…", async () =>
        {
            ComposeOutput.Clear();
            string logs = await ProjectIntegrations.ReadComposeLogsAsync(_project, service.Name, _closing.Token);
            if (_closing.IsCancellationRequested)
                return;
            ComposeOutput.Text = string.IsNullOrWhiteSpace(logs) ? "Gösterilecek log kaydı yok." : logs;
            ComposeOutput.ScrollToEnd();
            ComposeStatus.Text = $"{service.Name} · Son 200 log satırı (konteyner başına)";
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
        ? "Komutun döndürdüğü sonuç okunamadı." : ex.Message;
}
