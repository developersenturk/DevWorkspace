using System.Diagnostics;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private AppUpdateService? _appUpdates;
    private bool _appUpdateManualCheck;
    private bool _restartForAppUpdate;
    private AppUpdatePhase? _dismissedAppUpdatePhase;

    internal void StartUpdateChecks()
    {
        if (Program.UpdateManager is not { } manager) return;
        _appUpdates = new(new VelopackUpdateFeed(manager));
        _appUpdates.Changed += AppUpdateChanged;
        _ = RunAppUpdateChecksAsync();
        Closed += AppUpdateWindowClosed;
    }

    private async Task RunAppUpdateChecksAsync()
    {
        try
        {
            // Keep startup and workspace loading responsive.
            await Task.Delay(TimeSpan.FromSeconds(5), _closing.Token);
            do
            {
                await _appUpdates!.CheckAndDownloadAsync(_closing.Token);
                await Task.Delay(TimeSpan.FromHours(6), _closing.Token);
            } while (!_closing.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
    }

    private void AppUpdateChanged(AppUpdateStatus status)
    {
        if (Dispatcher.HasShutdownStarted || _closing.IsCancellationRequested) return;
        _ = Dispatcher.InvokeAsync(() => ShowAppUpdateStatus(status, _appUpdateManualCheck));
    }

    private void ShowAppUpdateStatus(AppUpdateStatus status, bool manual)
    {
        if (_closing.IsCancellationRequested) return;
        bool visible = status.Phase is AppUpdatePhase.Downloading or AppUpdatePhase.Ready || manual;
        if (!manual && _dismissedAppUpdatePhase == status.Phase) visible = false;
        AppUpdateBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateMessage.Text = status.Phase switch
        {
            AppUpdatePhase.Checking => AppUpdateText.Text("UpdateChecking"),
            AppUpdatePhase.Downloading => AppUpdateText.Format("UpdateDownloading", status.Version, status.Progress),
            AppUpdatePhase.Ready => AppUpdateText.Format("UpdateReady", status.Version),
            AppUpdatePhase.Failed => AppUpdateText.Text("UpdateFailed"),
            AppUpdatePhase.Unavailable => AppUpdateText.Text("UpdateUnavailable"),
            _ => AppUpdateText.Text("UpdateCurrent")
        };
        AppUpdateProgress.Visibility = status.Phase == AppUpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateProgress.Value = status.Progress;
        AppUpdateRestartButton.Visibility = status.Phase == AppUpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateRetryButton.Visibility = status.Phase == AppUpdatePhase.Failed ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateInstallButton.Visibility = status.Phase == AppUpdatePhase.Unavailable ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CheckAppUpdatesClick(object sender, RoutedEventArgs e)
    {
        _dismissedAppUpdatePhase = null;
        if (_appUpdates is null)
        {
            ShowAppUpdateStatus(new(AppUpdatePhase.Unavailable), manual: true);
            return;
        }
        if (_appUpdateManualCheck) return;
        _appUpdateManualCheck = true;
        try
        {
            ShowAppUpdateStatus(new(AppUpdatePhase.Checking), manual: true);
            await _appUpdates.CheckAndDownloadAsync(_closing.Token);
            ShowAppUpdateStatus(_appUpdates.Status, manual: true);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        finally { _appUpdateManualCheck = false; }
    }

    private void DismissAppUpdateClick(object sender, RoutedEventArgs e)
    {
        _dismissedAppUpdatePhase = _appUpdates?.Status.Phase;
        AppUpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void AppUpdateReleasesClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(VelopackUpdateFeed.RepositoryUrl + "/releases/latest") { UseShellExecute = true });

    private void RestartForAppUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_appUpdates?.Status.Phase != AppUpdatePhase.Ready || Program.UpdateLease is not { } lease) return;
        if (_busy || _workspaceCheck is not null || _gitView?.IsBusy == true || _savingExplorerFolder ||
            OperationCenter.Entries.Any(operation => operation.Cancel is not null))
        {
            AppUpdateMessage.Text = AppUpdateText.Text("UpdateBusy");
            return;
        }
        try
        {
            if (!lease.TryAcquireExclusive())
            {
                AppUpdateMessage.Text = AppUpdateText.Text("UpdateOtherInstance");
                return;
            }
            _restartForAppUpdate = true;
            // Uses the existing closing path to save workspace, draft, and layout state.
            Close();
            if (IsLoaded)
            {
                _restartForAppUpdate = false;
                lease.ReleaseExclusive();
            }
        }
        catch (Exception ex)
        {
            _restartForAppUpdate = false;
            lease.ReleaseExclusive();
            Trace.TraceWarning("Application update restart failed: {0}", ex);
            AppUpdateMessage.Text = AppUpdateText.Text("UpdateRestartFailed");
        }
    }

    private void AppUpdateWindowClosed(object? sender, EventArgs e)
    {
        if (_appUpdates is not null) _appUpdates.Changed -= AppUpdateChanged;
        if (!_restartForAppUpdate || Program.UpdateManager is not { } manager) return;
        try
        {
            // Only called AFTER the user-requested close and state save succeeded.
            // Never call this from the background checker: the SDK has a 60-second exit timeout.
            manager.WaitExitThenApplyUpdates(manager.UpdatePendingRestart, silent: true, restart: true,
                restartArgs: _workspace is { } workspace ? [workspace.Root] : []);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Application update apply failed: {0}", ex);
            MessageBox.Show(AppUpdateText.Text("UpdateRestartFailed"), "DevWorkspace", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
