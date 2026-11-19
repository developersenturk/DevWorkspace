using System.Diagnostics;
using System.IO;
using Velopack;

namespace DevWorkspace;

internal static class Program
{
    internal static UpdateManager? UpdateManager { get; private set; }
    internal static UpdateInstanceLease? UpdateLease { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // SDK hooks run before any WPF initialization. Apply explicitly below so a
        // second launch never lets the SDK terminate an already running instance.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        try
        {
            if (Environment.GetEnvironmentVariable("DEVWORKSPACE_SSH_ASKPASS") != "1")
                if (!InitializeUpdates(args)) return;
            var app = new App();
            app.InitializeComponent();
            app.Resources.MergedDictionaries.Add(AppUpdateText.CreateResources());
            app.Run();
        }
        finally { UpdateLease?.Dispose(); }
    }

    private static bool InitializeUpdates(string[] args)
    {
        bool installed = false;
        try
        {
            var manager = VelopackUpdateFeed.CreateManager();
            if (!manager.IsInstalled) return true; // Standalone Debug EXE still works normally.
            installed = true;
            var lease = new UpdateInstanceLease(AppContext.BaseDirectory,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DevWorkspace", "update-locks"));
            UpdateManager = manager;
            UpdateLease = lease;
            if (manager.UpdatePendingRestart is { } pending && lease.TryAcquireExclusive())
            {
                try { manager.ApplyUpdatesAndRestart(pending, args); }
                finally { lease.ReleaseExclusive(); }
            }
        }
        catch (Exception ex)
        {
            // An offline/broken update must never prevent launching the workspace.
            Trace.TraceWarning("Application update startup skipped: {0}", ex);
            UpdateManager = null;
            // Never run an installed copy without registering its lease: another
            // instance could otherwise replace its files while it is working.
            if (installed && UpdateLease is null)
            {
                System.Windows.MessageBox.Show("DevWorkspace could not acquire its update lock. Please try opening it again shortly.",
                    "DevWorkspace", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return false;
            }
        }
        return true;
    }
}
