using System.ComponentModel;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using DevExpress.Mvvm;

namespace DevWorkspace;

public partial class AppUpdateToast : UserControl
{
    internal static double MeasureHeight(string message, bool hasAction, bool downloading)
    {
        // Measure text directly: bindings on a detached preview do not resolve until
        // a dispatcher pass and would incorrectly reserve space for hidden actions.
        var text = new TextBlock
        {
            Text = message, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20
        };
        text.Measure(new Size(346, double.PositiveInfinity));
        // Border + padding + 28px heading and its 10px gap; optional progress/actions.
        return Math.Ceiling(68 + text.DesiredSize.Height + (hasAction ? 44 : 0) + (downloading ? 16 : 0));
    }

    public AppUpdateToast()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Give the popup host a useful accessible window name as well.
            if (Window.GetWindow(this) is { } host) host.Title = "DevWorkspace · " + AppUpdateText.Text("UpdateCheck");
        };
    }
}

internal sealed class AppUpdateToastModel(AppUpdatePhase phase, Action restart, Action retry, Action install, Action dismiss)
    : INotifyPropertyChanged
{
    private string _message = "";
    private int _progress;
    public string Message
    {
        get => _message;
        set { if (_message == value) return; _message = value; PropertyChanged?.Invoke(this, new(nameof(Message))); }
    }
    public int Progress
    {
        get => _progress;
        set { if (_progress == value) return; _progress = value; PropertyChanged?.Invoke(this, new(nameof(Progress))); }
    }
    public bool Downloading => phase == AppUpdatePhase.Downloading;
    public bool Ready => phase == AppUpdatePhase.Ready;
    public bool Failed => phase == AppUpdatePhase.Failed;
    public bool Unavailable => phase == AppUpdatePhase.Unavailable;
    public bool HasAction => Ready || Failed || Unavailable;
    public ICommand Restart { get; } = new DelegateCommand(restart);
    public ICommand Retry { get; } = new DelegateCommand(retry);
    public ICommand Install { get; } = new DelegateCommand(install);
    public ICommand Dismiss { get; } = new DelegateCommand(dismiss);
    public event PropertyChangedEventHandler? PropertyChanged;
}
