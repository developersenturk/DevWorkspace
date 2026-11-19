using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DevWorkspace;
using DevExpress.Xpf.Core;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static void Call(MainWindow main, string method) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(main, null);
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }

    [STAThread]
    public static void Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var theme in new[] { LightweightTheme.Win11Light, LightweightTheme.Win11Dark })
        foreach (bool populated in new[] { false, true })
        {
            ApplicationThemeHelper.ApplicationThemeName = theme.Name;
            var main = new MainWindow();
            // Exercise the real menu without scanning a workspace or saving the user's session.
            main.Loaded -= (RoutedEventHandler)typeof(MainWindow).GetMethod("OnLoaded", Private)!.CreateDelegate(typeof(RoutedEventHandler), main);
            Call(main, "StopWatchingProjectColumnWidths");
            var session = typeof(MainWindow).GetField("_session", Private)!.GetValue(main)!;
            var layouts = (IDictionary)session.GetType().GetProperty("SavedLayouts")!.GetValue(session)!;
            layouts.Clear();
            if (populated) { layouts["First"] = new byte[] { 1 }; layouts["Second"] = new byte[] { 2 }; }
            Call(main, "PopulateSavedLayoutsMenu");
            var menu = (MenuItem)main.FindName("WindowsMenu");
            var saved = (MenuItem)main.FindName("SavedLayoutsMenu");
            object[] original = saved.Items.Cast<object>().ToArray();
            main.Show(); main.UpdateLayout();
            for (int i = 0; i < 12; i++)
            {
                var elapsed = Stopwatch.StartNew();
                menu.IsSubmenuOpen = true;
                main.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Check(menu.IsSubmenuOpen, "Windows menu did not open");
                Check(original.SequenceEqual(saved.Items.Cast<object>()), "Opening Windows menu rebuilt live submenu controls");
                Check(saved.IsEnabled == populated, "Saved layout menu state is incorrect");
                if (populated)
                {
                    saved.IsSubmenuOpen = true;
                    main.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Check(original.SequenceEqual(saved.Items.Cast<object>()), "Opening child submenu rebuilt live controls");
                    saved.IsSubmenuOpen = false;
                }
                menu.IsSubmenuOpen = false;
                main.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "Menu interaction stalled the UI dispatcher");
            }
            main.Close();
        }
        app.Shutdown();
        Console.WriteLine($"Menu interaction: {checks} checks passed (empty/saved layouts, light/dark themes, repeated parent/child openings).");
    }
}
