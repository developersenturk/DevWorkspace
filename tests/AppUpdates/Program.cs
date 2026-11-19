using DevWorkspace;
using Velopack;
using Velopack.Locators;

if (args is ["--github", var livePackages])
{
    Directory.CreateDirectory(livePackages);
    var locator = new TestVelopackLocator("DevWorkspace", "0.9.0", livePackages);
    var manager = new UpdateManager(new Velopack.Sources.GithubSource(VelopackUpdateFeed.RepositoryUrl, null, false),
        new UpdateOptions { ExplicitChannel = VelopackUpdateFeed.Channel }, locator);
    var actual = new AppUpdateService(new VelopackUpdateFeed(manager));
    await actual.CheckAndDownloadAsync(default);
    if (actual.Status.Phase != AppUpdatePhase.Ready || manager.UpdatePendingRestart is null)
        throw new Exception("Published GitHub update was not verified: " + actual.Status);
    Console.WriteLine("PASS: public GitHub Releases download verified; staged version: " + actual.Status.Version);
    return;
}

if (args is ["--package", var feedPath, var packagesPath])
{
    // Real SDK check, download, and checksum verification against a local feed.
    Directory.CreateDirectory(packagesPath);
    var locator = new TestVelopackLocator("DevWorkspace", "1.0.0", packagesPath);
    var manager = new UpdateManager(feedPath, new UpdateOptions { ExplicitChannel = VelopackUpdateFeed.Channel }, locator);
    var actual = new AppUpdateService(new VelopackUpdateFeed(manager));
    await actual.CheckAndDownloadAsync(default);
    if (actual.Status.Phase != AppUpdatePhase.Ready || actual.Status.Version != "1.0.1")
        throw new Exception("Real SDK package download/verification failed: " + actual.Status);
    Console.WriteLine("PASS: real Velopack SDK downloaded and verified 1.0.1; pending restart: " + manager.UpdatePendingRestart?.Version);
    return;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var feed = new FakeFeed { IsInstalled = false };
var service = new AppUpdateService(feed);
await service.CheckAndDownloadAsync(default);
Require(service.Status.Phase == AppUpdatePhase.Unavailable && feed.Checks == 0, "Standalone copies must not check the network.");

feed.IsInstalled = true;
await service.CheckAndDownloadAsync(default);
Require(service.Status.Phase == AppUpdatePhase.Idle && feed.Downloads == 0, "No release must not download anything.");

var states = new List<AppUpdatePhase>();
service.Changed += status => states.Add(status.Phase);
feed.Release = new("1.1.0", new object());
await service.CheckAndDownloadAsync(default);
Require(service.Status == new AppUpdateStatus(AppUpdatePhase.Ready, "1.1.0", 100), "Verified download should become pending.");
Require(states.Contains(AppUpdatePhase.Downloading) && feed.Downloads == 1, "Download must publish progress.");

int checks = feed.Checks;
feed.PendingVersion = "1.1.0";
await service.CheckAndDownloadAsync(default);
Require(feed.Checks == checks && service.Status.Phase == AppUpdatePhase.Ready, "Pending updates must survive a service restart without another network request.");

feed.PendingVersion = null;
feed.CheckFailure = true;
await service.CheckAndDownloadAsync(default);
Require(service.Status.Phase == AppUpdatePhase.Failed, "Offline check must be recoverable.");
feed.CheckFailure = false;
feed.DownloadFailure = true;
await service.CheckAndDownloadAsync(default);
Require(service.Status.Phase == AppUpdatePhase.Failed, "An invalid package must never become ready.");
feed.DownloadFailure = false;
await service.CheckAndDownloadAsync(default);
Require(service.Status.Phase == AppUpdatePhase.Ready, "A later retry must succeed.");

using (var canceled = new CancellationTokenSource())
{
    feed.DuringDownload = canceled.Cancel;
    await service.CheckAndDownloadAsync(canceled.Token);
    Require(service.Status.Phase != AppUpdatePhase.Ready, "Cancellation must not announce an unapplied partial download as ready.");
    feed.DuringDownload = null;
}

feed.CheckBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
checks = feed.Checks;
var first = service.CheckAndDownloadAsync(default);
await service.CheckAndDownloadAsync(default);
Require(feed.Checks == checks + 1, "Concurrent checks must be coalesced.");
feed.CheckBarrier.SetResult();
await first;

string root = Path.Combine(Path.GetTempPath(), "DevWorkspace-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var one = new UpdateInstanceLease(root, root);
    using (var two = new UpdateInstanceLease(root, root))
        Require(!one.TryAcquireExclusive(), "A second running copy must prevent applying an update.");
    Require(one.TryAcquireExclusive(), "The last copy must be able to acquire the update lease.");
    one.ReleaseExclusive();
    using var three = new UpdateInstanceLease(root, root);
    Require(!one.TryAcquireExclusive(), "Releasing an update lease must allow another running reader.");
}
finally { Directory.Delete(root, recursive: true); }

Console.WriteLine("PASS: update staging, progress, pending recovery, offline retry, invalid packages, cancellation, concurrent checks, and multi-instance protection.");

sealed class FakeFeed : IAppUpdateFeed
{
    public bool IsInstalled { get; set; } = true;
    public string? PendingVersion { get; set; }
    internal AppUpdateRelease? Release { get; set; }
    internal bool CheckFailure { get; set; }
    internal bool DownloadFailure { get; set; }
    internal Action? DuringDownload { get; set; }
    internal TaskCompletionSource? CheckBarrier { get; set; }
    internal int Checks, Downloads;

    public async Task<AppUpdateRelease?> CheckAsync()
    {
        Checks++;
        if (CheckBarrier is { } barrier) await barrier.Task;
        if (CheckFailure) throw new IOException("Simulated offline response.");
        return Release;
    }

    public Task DownloadAsync(AppUpdateRelease release, Action<int> progress, CancellationToken token)
    {
        Downloads++;
        progress(50);
        DuringDownload?.Invoke();
        token.ThrowIfCancellationRequested();
        if (DownloadFailure) throw new IOException("Simulated package verification failure.");
        progress(100);
        return Task.CompletedTask;
    }
}
