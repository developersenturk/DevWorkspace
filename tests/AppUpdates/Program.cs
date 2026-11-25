using DevWorkspace;
using Velopack;
using Velopack.Locators;
using System.Net;
using System.Text;

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

// Release notes must remain useful with no network and must never surface drafts.
string notesDirectory = Path.Combine(Path.GetTempPath(), "DevWorkspace-notes-" + Guid.NewGuid().ToString("N"));
try
{
    using var notesClient = new HttpClient(new ReleaseNotesHandler());
    var notes = new AppReleaseNotes(notesClient, notesDirectory);
    Require(notes.Local().Single().Version == AppReleaseNotes.CurrentVersion && notes.Local()[0].Body.Length > 100, "Bundled notes must work before any network request");
    Require(notes.HasUnread, "A new version must be marked unread");
    notes.MarkRead();
    Require(!new AppReleaseNotes(notesClient, notesDirectory).HasUnread, "Read state must survive reopening");
    var recent = await notes.RefreshAsync(default);
    Require(recent.Count == 3 && recent[0].Version == "900.0.1", "Only stable valid releases should be returned, newest first");
    Require(recent.Count(r => r.Version == AppReleaseNotes.CurrentVersion) == 1, "Current release must appear exactly once");
    Require(recent.Single(r => r.Version == "0.0.1").Body == "**Older** notes", "Markdown must survive retrieval");
    using var offlineClient = new HttpClient(new ReleaseNotesHandler { Offline = true });
    var offline = new AppReleaseNotes(offlineClient, notesDirectory);
    try { await offline.RefreshAsync(default); throw new Exception("Offline failure was not reported"); }
    catch (HttpRequestException) { }
    Require(offline.Local().Count == 3, "Cached notes must survive an offline refresh");
    File.WriteAllText(Path.Combine(notesDirectory, "release-notes.json"), "broken JSON");
    Require(offline.Local().Single().Version == AppReleaseNotes.CurrentVersion, "Corrupt cache must fall back to bundled notes");
    using var canceledNotes = new CancellationTokenSource(); canceledNotes.Cancel();
    try { await notes.RefreshAsync(canceledNotes.Token); throw new Exception("Notes refresh ignored cancellation"); }
    catch (OperationCanceledException) { }
    Console.WriteLine("PASS: release notes offline fallback, stable releases, ordering, Markdown, cache recovery, read state and cancellation.");
}
finally { if (Directory.Exists(notesDirectory)) Directory.Delete(notesDirectory, true); }

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

Require(!VelopackUpdateFeed.IsNewer("1.1.3", "1.2.0"), "A pending older package must not replace a newer executable.");
Require(!VelopackUpdateFeed.IsNewer("1.2.0", "1.2.0"), "The running version must not be reapplied.");
Require(VelopackUpdateFeed.IsNewer("1.2.1", "1.2.0"), "A newer patch must remain eligible.");
Require(!VelopackUpdateFeed.IsNewer("1.2.0-beta.1", "1.2.0"), "A prerelease must not replace the corresponding stable version.");
Require(!VelopackUpdateFeed.IsNewer("broken", "1.2.0"), "An invalid pending version must not apply.");
Console.WriteLine("PASS: older, equal, invalid and prerelease packages cannot replace the running stable executable.");

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

sealed class ReleaseNotesHandler : HttpMessageHandler
{
    internal bool Offline;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Offline) throw new HttpRequestException("Offline");
        if (request.Headers.UserAgent.Count == 0) throw new Exception("GitHub User-Agent missing");
        string json = """
        [
          {"tag_name":"v900.0.1","name":"New release","body":"## Changes","draft":false,"prerelease":false,"published_at":"2026-11-20T00:00:00Z"},
          {"tag_name":"v901.0.0","name":"Draft","body":"hidden","draft":true,"prerelease":false,"published_at":null},
          {"tag_name":"v902.0.0","name":"Preview","body":"hidden","draft":false,"prerelease":true,"published_at":null},
          {"tag_name":"other-tag","name":"Invalid","body":"hidden","draft":false,"prerelease":false,"published_at":null},
          {"tag_name":"v0.0.1","name":"Older","body":"**Older** notes","draft":false,"prerelease":false,"published_at":null}
        ]
        """;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
