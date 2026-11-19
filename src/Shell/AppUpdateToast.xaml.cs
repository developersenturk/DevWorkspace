using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using DevExpress.Mvvm;

namespace DevWorkspace;

public partial class AppUpdateToast : UserControl
{
    public AppUpdateToast() => InitializeComponent();
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
