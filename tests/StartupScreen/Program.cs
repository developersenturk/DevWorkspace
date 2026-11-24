using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DevWorkspace;
using DevExpress.Xpf.Core;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    public static void Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var type = typeof(App).Assembly.GetType("DevWorkspace.StartupScreen", true)!;
        foreach (var theme in new[] { LightweightTheme.Win11Light, LightweightTheme.Win11Dark })
        foreach (bool cancel in new[] { false, true })
        {
            ApplicationThemeHelper.ApplicationThemeName = theme.Name;
            var palette = (ResourceDictionary)typeof(App).GetField("_palette", Instance)!.GetValue(app)!;
            bool cancelled = false;
            object screen = Activator.CreateInstance(type, Instance, null,
                [palette, (Action)(() => cancelled = true)], null)!;
            object? Call(string name, params object[] args) => type.GetMethod(name, Instance)!.Invoke(screen, args);
            try
            {
                // Deliberately never pump the application dispatcher: construction/loading can block it.
                ((Task)Call("ShowAsync")!).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var window = (Window)type.GetField("_window", Instance)!.GetValue(screen)!;
                if (window.Dispatcher == app.Dispatcher) throw new Exception("Splash shares the blocked application dispatcher.");
                ((Task)Call("SetStageAsync", 1)!).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                var positions = new HashSet<int>();
                for (int i = 0; i < 12; i++)
                {
                    positions.Add(window.Dispatcher.Invoke(() =>
                    {
                        var progress = (ProgressBar)window.FindName("LoadingProgress");
                        if (progress.IsIndeterminate != SystemParameters.ClientAreaAnimation)
                            throw new Exception("Windows motion preference was ignored.");
                        if (!progress.IsIndeterminate) return (int)progress.Value;
                        var pulse = (Border)progress.Template.FindName("Pulse", progress);
                        return (int)((TranslateTransform)pulse.RenderTransform).X;
                    }));
                    Thread.Sleep(80);
                }
                if (SystemParameters.ClientAreaAnimation && positions.Count < 6)
                    throw new Exception($"Animation stalled while application dispatcher was blocked: {positions.Count} positions.");
                using var stopped = new ManualResetEventSlim();
                window.Dispatcher.Invoke(() => window.Dispatcher.ShutdownFinished += (_, _) => stopped.Set());
                if (cancel) window.Dispatcher.Invoke(window.Close);
                else
                {
                    ((Task<bool>)Call("CompleteAsync")!).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    Call("Close");
                }
                if (!stopped.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Startup dispatcher did not shut down.");
                if (cancelled != cancel) throw new Exception("Cancellation/completion callbacks were incorrect.");
                Console.WriteLine($"PASS {theme.Name}, cancel={cancel}: {positions.Count} animation positions with main dispatcher blocked; clean shutdown.");
            }
            finally { Call("Close"); }
        }
        app.Shutdown();
    }
}
