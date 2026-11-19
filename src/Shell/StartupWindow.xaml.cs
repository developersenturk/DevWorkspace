using System.Windows;
using System.Windows.Input;

namespace DevWorkspace;

public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "v" + typeof(StartupWindow).Assembly.GetName().Version?.ToString(3);
        LoadingProgress.IsIndeterminate = SystemParameters.ClientAreaAnimation;
        SetStage(0);
    }

    internal void SetStage(int stage)
    {
        StatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty,
            stage switch { 0 => "StartupPreparing", 1 => "StartupLoading", _ => "StartupReady" });
        ProjectsStage.Opacity = stage >= 1 ? 1 : 0.5;
        WorkspaceStage.Opacity = stage >= 2 ? 1 : 0.5;
        foreach (var (number, active) in new[] { (InterfaceNumber, stage == 0), (ProjectsNumber, stage == 1), (WorkspaceNumber, stage >= 2) })
        {
            number.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, active ? "BadgeAccentSurface" : "SurfaceRaised");
            number.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, active ? "Focus" : "Line");
        }
        if (!SystemParameters.ClientAreaAnimation) LoadingProgress.Value = stage * 50;
    }

    private void HeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && !ReferenceEquals(source, sender))
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        DragMove();
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
