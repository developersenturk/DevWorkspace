using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class App : Application
{
    private const int WindowCloakAttribute = 13;
    private ResourceDictionary? _palette;

    static App()
    {
        CompatibilitySettings.UseLightweightThemes = true;
        LightweightThemeManager.AllowStandardControlsTheming = true;
    }

    public App()
    {
        var session = SessionState.Load();
        LightweightThemeManager.CurrentThemeChanged += (_, _) => UpdatePalette();
        var saved = LightweightThemeManager.Themes.FirstOrDefault(theme => theme.ShowInThemeSelector && theme.Name == session.ThemeName);
        ApplyTheme(saved ?? (session.DarkTheme ? LightweightTheme.Win11Dark : LightweightTheme.Win11Light));
        if (_palette is null)
            UpdatePalette();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (MainWindow is not null)
            return;

        var splash = new StartupWindow();
        bool finished = false;
        splash.Closed += (_, _) =>
        {
            if (!finished)
                Shutdown();
        };
        splash.Show();

        try
        {
            // Give the preparation window a frame before constructing the dock and grids.
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (Dispatcher.HasShutdownStarted)
                return;

            var main = new MainWindow { ShowInTaskbar = false };
            main.ShowActivated = main.WindowState == WindowState.Maximized;
            MainWindow = main;
            var interop = new WindowInteropHelper(main);
            main.SourceInitialized += (_, _) => SetWindowCloak(interop.Handle, true);
            main.Show();
            try
            {
                await main.StartupReady;
            }
            catch (OperationCanceledException) when (Dispatcher.HasShutdownStarted || !main.IsLoaded)
            {
                if (!Dispatcher.HasShutdownStarted)
                    splash.Close();
                return;
            }
            catch (Exception ex)
            {
                main.ShowStartupError(ex.Message);
            }

            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (Dispatcher.HasShutdownStarted)
                return;
            if (!main.IsLoaded)
            {
                splash.Close();
                return;
            }

            finished = true;
            main.ShowInTaskbar = true;
            SetWindowCloak(interop.Handle, false);
            if (splash.IsActive)
                main.Activate();
            splash.Close();
        }
        catch (OperationCanceledException) when (Dispatcher.HasShutdownStarted) { }
        catch (Exception ex) when (!Dispatcher.HasShutdownStarted)
        {
            System.Diagnostics.Trace.TraceError(ex.ToString());
            finished = true;
            MessageBox.Show(splash, $"DevWorkspace başlatılamadı.\n\n{ex.Message}", "DevWorkspace",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void SetWindowCloak(nint handle, bool cloak)
    {
        int value = cloak ? 1 : 0;
        DwmSetWindowAttribute(handle, WindowCloakAttribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    internal static void ApplyTheme(LightweightTheme theme) => ApplicationThemeHelper.ApplicationThemeName = theme.Name;

    private void UpdatePalette()
    {
        var theme = LightweightThemeManager.CurrentTheme ?? LightweightTheme.Win11Light;
        var palette = CreatePalette(theme);
        if (_palette is null)
            Resources.MergedDictionaries.Insert(0, palette);
        else
            Resources.MergedDictionaries[Resources.MergedDictionaries.IndexOf(_palette)] = palette;
        _palette = palette;
    }

    private static ResourceDictionary CreatePalette(LightweightTheme theme)
    {
        // These aliases let the workbench and document viewer share the control theme.
        Brush ThemeBrush(string key, string? alternate = null)
        {
            var color = theme.Palette[$"Color.{key}"] is Color modern
                ? modern : (Color)theme.Palette[$"Color.{alternate}"];
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        Brush ink = ThemeBrush("Foreground.Primary", "Foreground");
        Brush window = ThemeBrush("WindowBackground", "Window.Background");
        Brush panel = ThemeBrush("PanelBackground", "Control.Background");
        Brush line = ThemeBrush("PanelBorder", "Border");
        Brush raised = ThemeBrush("Button.SimpleButtonBackground", "Button.Background");
        Color windowColor = ((SolidColorBrush)window).Color;
        bool dark = 0.213 * windowColor.ScR + 0.715 * windowColor.ScG + 0.072 * windowColor.ScB < 0.35;
        Brush BadgeBrush(string lightColor, string darkColor)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : lightColor));
            brush.Freeze();
            return brush;
        }

        var hatch = new DrawingBrush(new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing(raised, null, Geometry.Parse("M0,0 H7 V7 H0 Z")),
                new GeometryDrawing(null, new Pen(line, 1), Geometry.Parse("M0,7 L7,0 M-1,1 L1,-1 M6,8 L8,6"))
            }
        })
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 7, 7),
            Viewbox = new Rect(0, 0, 7, 7)
        };
        hatch.Freeze();

        return new ResourceDictionary
        {
            ["Ink"] = ink,
            ["Muted"] = ThemeBrush("Foreground.Muted", "Foreground"),
            ["Line"] = line,
            ["Divider"] = ThemeBrush("Separator", "Delimiter"),
            ["Canvas"] = ThemeBrush("Editor.Background"),
            ["Shell"] = window,
            ["PanelSoft"] = panel,
            ["SurfaceRaised"] = raised,
            ["Hover"] = ThemeBrush("ListItem.HoverBackground", "HoverBackground"),
            ["Focus"] = ThemeBrush("Button.CheckedBorder", "Focused"),
            ["NeutralSurface"] = raised,
            ["BadgeAccentSurface"] = BadgeBrush("#E8E3FF", "#43366F"),
            ["BadgeAccentText"] = BadgeBrush("#422797", "#E9DFFF"),
            ["BadgeSuccessSurface"] = BadgeBrush("#DCF3E6", "#214B38"),
            ["BadgeSuccessText"] = BadgeBrush("#185D3D", "#C0F3D1"),
            ["BadgeWarningSurface"] = BadgeBrush("#FFF0C9", "#594315"),
            ["BadgeWarningText"] = BadgeBrush("#754900", "#FFE6A3"),
            ["BadgeErrorSurface"] = BadgeBrush("#FBE0E4", "#63303C"),
            ["BadgeErrorText"] = BadgeBrush("#8C2338", "#FFD6DF"),
            ["BadgeNeutralSurface"] = BadgeBrush("#E9EDF3", "#343D4B"),
            ["BadgeNeutralText"] = BadgeBrush("#465368", "#D8E0EB"),
            ["WorkspaceBackground"] = window,
            ["MissingDiffLines"] = hatch
        };
    }
}
