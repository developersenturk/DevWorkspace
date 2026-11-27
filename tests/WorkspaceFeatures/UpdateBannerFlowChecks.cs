using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevWorkspace;

internal static class UpdateBannerFlowChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var window = new MainWindow();
        window.Loaded -= (RoutedEventHandler)typeof(MainWindow).GetMethod("OnLoaded", flags)!.CreateDelegate(typeof(RoutedEventHandler), window);
        typeof(MainWindow).GetMethod("StopWatchingProjectColumnWidths", flags)!.Invoke(window, null);
        var updates = new AppUpdateService(new Feed());
        typeof(MainWindow).GetField("_appUpdates", flags)!.SetValue(window, updates);
        updates.Changed += (Action<AppUpdateStatus>)typeof(MainWindow).GetMethod("AppUpdateChanged", flags)!.CreateDelegate(typeof(Action<AppUpdateStatus>), window);
        window.Show();
        typeof(MainWindow).GetMethod("ShowAppUpdateStatus", flags)!.Invoke(window, new object[] { new AppUpdateStatus(AppUpdatePhase.Checking), false });
        window.UpdateLayout();
        var banner = (Border)window.FindName("AppUpdateBanner");
        check(banner.IsVisible && ((Button)window.FindName("AppUpdateBannerAction")).Visibility == Visibility.Collapsed,
            "Automatic check is visible immediately while release information is loading");
        var operation = Task.Run(() => updates.CheckAndDownloadAsync(default));
        while (!operation.IsCompleted)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        operation.GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        check(updates.Status.Phase == AppUpdatePhase.Ready, "Real background service completes download");
        check(banner.IsVisible && banner.ActualHeight > 0, "Service events display the update banner in the live window");
        check(((TextBlock)window.FindName("AppUpdateBannerTitle")).Text.Contains("9.9.9"), "Service event preserves the available version");
        window.UpdateAppUpdateBanner(new(AppUpdatePhase.Idle));
        window.UpdateAppUpdateBanner(new(AppUpdatePhase.Checking));
        window.UpdateAppUpdateBanner(new(AppUpdatePhase.Failed));
        check(banner.IsVisible && ((Button)window.FindName("AppUpdateBannerAction")).Visibility == Visibility.Visible,
            "Failed startup checks remain visible with a retry action");
        window.Close();
    }

    private sealed class Feed : IAppUpdateFeed
    {
        public bool IsInstalled => true;
        public string? PendingVersion => null;
        public Task<AppUpdateRelease?> CheckAsync() => Task.FromResult<AppUpdateRelease?>(new("9.9.9", new object()));
        public Task DownloadAsync(AppUpdateRelease release, Action<int> progress, CancellationToken token)
        {
            progress(50); progress(100);
            return Task.CompletedTask;
        }
    }
}
