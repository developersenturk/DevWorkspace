namespace DevWorkspace;

internal enum AppUpdatePhase { Idle, Checking, Downloading, Ready, Failed, Unavailable }
internal sealed record AppUpdateStatus(AppUpdatePhase Phase, string? Version = null, int Progress = 0);
internal sealed record AppUpdateRelease(string Version, object NativeUpdate);

// Intentionally has no apply/exit API. Background checks can only stage updates.
internal interface IAppUpdateFeed
{
    bool IsInstalled { get; }
    string? PendingVersion { get; }
    Task<AppUpdateRelease?> CheckAsync();
    Task DownloadAsync(AppUpdateRelease release, Action<int> progress, CancellationToken token);
}

internal sealed class AppUpdateService(IAppUpdateFeed feed)
{
    private readonly SemaphoreSlim _checking = new(1, 1);
    internal AppUpdateStatus Status { get; private set; } = new(AppUpdatePhase.Idle);
    internal event Action<AppUpdateStatus>? Changed;

    internal async Task CheckAndDownloadAsync(CancellationToken token)
    {
        if (!await _checking.WaitAsync(0, token)) return;
        try
        {
            if (!feed.IsInstalled)
            {
                Report(new(AppUpdatePhase.Unavailable));
                return;
            }
            if (feed.PendingVersion is { } pending)
            {
                Report(new(AppUpdatePhase.Ready, pending, 100));
                return;
            }
            Report(new(AppUpdatePhase.Checking));
            // The SDK check has no cancellation overload. Await it fully so a canceled
            // caller never leaves an unobserved check racing the next one.
            var release = await feed.CheckAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (release is null)
            {
                Report(new(AppUpdatePhase.Idle));
                return;
            }
            Report(new(AppUpdatePhase.Downloading, release.Version));
            await feed.DownloadAsync(release,
                percent => Report(new(AppUpdatePhase.Downloading, release.Version, Math.Clamp(percent, 0, 100))),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // DownloadUpdatesAsync completes only after the SDK's package verification.
            Report(new(AppUpdatePhase.Ready, release.Version, 100));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Application update check failed: {0}", ex);
            Report(new(AppUpdatePhase.Failed));
        }
        finally { _checking.Release(); }
    }

    private void Report(AppUpdateStatus status)
    {
        Status = status;
        Changed?.Invoke(status);
    }
}
