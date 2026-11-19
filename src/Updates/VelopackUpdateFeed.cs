using Velopack;
using Velopack.Sources;

namespace DevWorkspace;

internal sealed class VelopackUpdateFeed(UpdateManager manager) : IAppUpdateFeed
{
    internal const string RepositoryUrl = "https://github.com/developersenturk/DevWorkspace";
    internal const string Channel = "win-x64";

    internal static UpdateManager CreateManager() => new(
        new GithubSource(RepositoryUrl, null, prerelease: false),
        new UpdateOptions { ExplicitChannel = Channel, AllowVersionDowngrade = false });

    public bool IsInstalled => manager.IsInstalled;
    public string? PendingVersion => manager.UpdatePendingRestart?.Version.ToString();

    public async Task<AppUpdateRelease?> CheckAsync()
    {
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return update is null ? null : new(update.TargetFullRelease.Version.ToString(), update);
    }

    public Task DownloadAsync(AppUpdateRelease release, Action<int> progress, CancellationToken token) =>
        manager.DownloadUpdatesAsync((UpdateInfo)release.NativeUpdate, progress, cancelToken: token);
}
