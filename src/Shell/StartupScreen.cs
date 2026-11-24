using System.Windows;
using System.Windows.Threading;

namespace DevWorkspace;

// Keep startup feedback responsive while the main dispatcher builds the dock and grids.
internal sealed class StartupScreen
{
    private readonly TaskCompletionSource<StartupWindow> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StartupWindow? _window;
    private bool _completed;

    internal StartupScreen(ResourceDictionary palette, Action cancelled)
    {
        // Only frozen brushes cross dispatchers; each window owns its resource dictionaries.
        var colors = palette.Keys.Cast<object>().ToDictionary(key => key, key => palette[key]);
        var thread = new Thread(() =>
        {
            try
            {
                var resources = Loc.CreateResources();
                foreach (var (key, value) in colors) resources[key] = value;
                var window = new StartupWindow(resources);
                _window = window;
                window.Closed += (_, _) =>
                {
                    if (!_completed) cancelled();
                    window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                window.ContentRendered += Rendered;
                void Rendered(object? sender, EventArgs e)
                {
                    window.ContentRendered -= Rendered;
                    _ready.TrySetResult(window);
                }
                window.Show();
                Dispatcher.Run();
                _ready.TrySetCanceled();
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
                if (_window is { IsVisible: true }) _window.Close();
            }
        }) { IsBackground = true, Name = "Startup screen" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal async Task ShowAsync() => await _ready.Task;

    internal async Task SetStageAsync(int stage)
    {
        var window = await _ready.Task;
        await window.Dispatcher.InvokeAsync(() => window.SetStage(stage));
    }

    internal async Task<bool> CompleteAsync()
    {
        var window = await _ready.Task;
        return await window.Dispatcher.InvokeAsync(() =>
        {
            _completed = true;
            window.SetStage(2);
            return window.IsActive;
        });
    }

    internal void Close()
    {
        if (_window is not { } window || window.Dispatcher.HasShutdownStarted) return;
        window.Dispatcher.BeginInvoke(() =>
        {
            _completed = true;
            window.Close();
        });
    }
}
