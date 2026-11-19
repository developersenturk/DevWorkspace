using System.IO;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace DevWorkspace;

internal static class ReleaseDownloadVerifier
{
    internal static async Task VerifyAsync(string repository, string appId, ReleaseStore store, Action<string> log, CancellationToken token)
    {
        ReleaseRules.AppId(appId);
        string repo = ReleaseRules.Repository(repository);
        string folder = Path.Combine(store.DirectoryPath, "download-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var locator = new TestVelopackLocator(appId, "0.0.0", folder);
        var manager = new UpdateManager(new GithubSource("https://github.com/" + repo, null, false),
            new UpdateOptions { ExplicitChannel = "win-x64" }, locator);
        var updater = new AppUpdateService(new VelopackUpdateFeed(manager));
        updater.Changed += state => log($"{state.Phase} · {state.Version} · {state.Progress}%");
        await updater.CheckAndDownloadAsync(token);
        if (updater.Status.Phase != AppUpdatePhase.Ready || manager.UpdatePendingRestart is null)
            throw new IOException(ReleaseText.T("ProbeFailed"));
        log(ReleaseText.T("ProbePassed") + " " + updater.Status.Version + Environment.NewLine + folder);
    }
}
