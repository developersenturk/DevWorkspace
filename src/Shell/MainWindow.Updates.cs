using System.Diagnostics;
using System.Windows;
using DevExpress.Mvvm;

namespace DevWorkspace;

public partial class MainWindow
{
    private AppUpdateService? _appUpdates;
    private bool _appUpdateManualCheck;
    private bool _restartForAppUpdate;
    private AppUpdatePhase? _dismissedAppUpdatePhase;
    private INotification? _appUpdateNotification;
    private AppUpdateToastModel? _appUpdateToast;
    private AppUpdateStatus? _displayedAppUpdate;

    internal void StartUpdateChecks()
    {
        if (Program.UpdateManager is not { } manager) return;
        _appUpdates = new(new VelopackUpdateFeed(manager));
        _appUpdates.Changed += AppUpdateChanged;
        _ = RunAppUpdateChecksAsync();
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
        RestartAppUpdateMenu.Visibility = status.Phase == AppUpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) { HideAppUpdateToast(); return; }
        string message = status.Phase switch
        {
            AppUpdatePhase.Checking => AppUpdateText.Text("UpdateChecking"),
            AppUpdatePhase.Downloading => AppUpdateText.Format("UpdateDownloading", status.Version, status.Progress),
            AppUpdatePhase.Ready => AppUpdateText.Format("UpdateReady", status.Version),
            AppUpdatePhase.Failed => AppUpdateText.Text("UpdateFailed"),
            AppUpdatePhase.Unavailable => AppUpdateText.Text("UpdateUnavailable"),
            _ => AppUpdateText.Text("UpdateCurrent")
        };
        // Progress updates mutate the current toast rather than queueing a new popup.
        if (_appUpdateToast is not null && _displayedAppUpdate?.Phase == status.Phase && _displayedAppUpdate.Version == status.Version)
        {
            _appUpdateToast.Message = message;
            _appUpdateToast.Progress = status.Progress;
            return;
        }
        HideAppUpdateToast();
        _displayedAppUpdate = status;
        _appUpdateToast = new(status.Phase,
            () => RestartForAppUpdateClick(this, new RoutedEventArgs()),
            () => CheckAppUpdatesClick(this, new RoutedEventArgs()),
            () => AppUpdateReleasesClick(this, new RoutedEventArgs()),
            () => DismissAppUpdateClick(this, new RoutedEventArgs())) { Message = message, Progress = status.Progress };
        AppUpdateNotifications.CustomNotificationDuration = status.Phase switch
        {
            AppUpdatePhase.Downloading or AppUpdatePhase.Checking => TimeSpan.FromDays(1),
            AppUpdatePhase.Ready or AppUpdatePhase.Unavailable => TimeSpan.FromSeconds(12),
            AppUpdatePhase.Failed => TimeSpan.FromSeconds(8),
            _ => TimeSpan.FromSeconds(4)
        };
        var notification = AppUpdateNotifications.CreateCustomNotification(_appUpdateToast);
        _appUpdateNotification = notification;
        _ = ObserveAppUpdateToastAsync(notification);
    }

    private async Task ObserveAppUpdateToastAsync(INotification notification)
    {
        try { await notification.ShowAsync(); }
        catch (Exception ex)
        {
            Trace.TraceWarning("Update notification failed: {0}", ex);
            if (!_closing.IsCancellationRequested) StatusText.Text = AppUpdateText.Text("UpdateNotificationFailed");
        }
        finally
        {
            if (ReferenceEquals(_appUpdateNotification, notification))
            {
                _appUpdateNotification = null;
                _appUpdateToast = null;
            }
        }
    }

    private void HideAppUpdateToast()
    {
        var notification = _appUpdateNotification;
        _appUpdateNotification = null;
        _appUpdateToast = null;
        notification?.Hide();
    }

    private void ShowAppUpdateActionMessage(string key)
    {
        if (_appUpdates is not null) ShowAppUpdateStatus(_appUpdates.Status, manual: true);
        if (_appUpdateToast is not null) _appUpdateToast.Message = AppUpdateText.Text(key);
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
        HideAppUpdateToast();
    }

    private void AppUpdateReleasesClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(VelopackUpdateFeed.RepositoryUrl + "/releases/latest") { UseShellExecute = true });

    private void RestartForAppUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_appUpdates?.Status.Phase != AppUpdatePhase.Ready || Program.UpdateLease is not { } lease) return;
        if (_busy || _workspaceCheck is not null || _gitView?.IsBusy == true || _savingExplorerFolder ||
            OperationCenter.Entries.Any(operation => operation.Cancel is not null))
        {
            ShowAppUpdateActionMessage("UpdateBusy");
            return;
        }
        try
        {
            if (!lease.TryAcquireExclusive())
            {
                ShowAppUpdateActionMessage("UpdateOtherInstance");
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
            ShowAppUpdateActionMessage("UpdateRestartFailed");
        }
    }

    private void AppUpdateWindowClosed(object? sender, EventArgs e)
    {
        HideAppUpdateToast();
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
