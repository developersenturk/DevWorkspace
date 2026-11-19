using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    internal void StartInstallationStatistics()
    {
        var telemetry = InstallationTelemetry.Current;
        if (telemetry.Load().Enabled is null)
        {
            bool enabled = MessageBox.Show(this, Loc.Text("TelemetryConsent"), Loc.Text("TelemetryTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!telemetry.SetEnabled(enabled)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_closing.IsCancellationRequested)
                {
                    await telemetry.SendAsync(DateTimeOffset.UtcNow, _closing.Token);
                    await Task.Delay(TimeSpan.FromHours(1), _closing.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }
}
