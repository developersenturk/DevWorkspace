using System.IO;
using DevWorkspace;

internal static class GitAmendChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "DevWorkspace-amend-" + Guid.NewGuid().ToString("N"));
        string repository = Path.Combine(root, "work");
        string remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(repository);
        async Task<string> Git(params string[] args)
        {
            var (code, output, error) = await GitProjectOperations.RunAsync(repository, args, default);
            if (code != 0) throw new Exception(error);
            return output.TrimEnd('\r', '\n');
        }
        async Task Rejected(Func<Task> action, string message)
        {
            try { await action(); }
            catch (IOException) { check(true, message); return; }
            throw new Exception(message);
        }
        try
        {
            await Git("init", "-q");
            await Git("config", "user.name", "Amend Test");
            await Git("config", "user.email", "amend@example.invalid");
            await Git("config", "commit.gpgSign", "false");
            var workbench = new GitWorkbench(repository);
            var empty = await workbench.ReadAsync(default);
            check(empty.AmendBlockReason is not null, "Amend disabled before the first commit");
            await Rejected(() => workbench.AmendAsync(empty, "message", default), "Cannot amend an unborn branch");
            string tracked = Path.Combine(repository, "tracked.txt");
            await File.WriteAllTextAsync(tracked, "original\n");
            await Git("add", ".");
            await Git("commit", "-qm", "Initial message", "--author", "Original Author <author@example.invalid>");
            var initial = await workbench.ReadAsync(default);
            check(initial.AmendBlockReason is null && initial.IndexSnapshot.Length == 0, "Clean repositories can amend messages");
            check(await workbench.ReadLastCommitMessageAsync(initial, default) == "Initial message", "Last commit message is loaded");
            string message = "Edited subject\n\nBody with \"quotes\", $value and --flags";
            await workbench.AmendAsync(initial, message, default);
            string amended = await Git("rev-parse", "HEAD");
            check(amended != initial.Status.CommitId && await Git("rev-list", "--count", "HEAD") == "1", "Message-only amend replaces the root commit");
            check(await Git("log", "-1", "--format=%B") == message, "Multiline messages and literal punctuation survive");
            check(await Git("log", "-1", "--format=%an <%ae>") == "Original Author <author@example.invalid>", "Amend preserves the original author");
            await File.WriteAllTextAsync(tracked, "staged content\n");
            await Git("add", "tracked.txt");
            await File.AppendAllTextAsync(tracked, "unstaged content\n");
            var staged = await workbench.ReadAsync(default);
            await workbench.AmendAsync(staged, "Include staged content", default);
            check(await Git("show", "HEAD:tracked.txt") == "staged content", "Only staged content enters the amended commit");
            check((await File.ReadAllTextAsync(tracked)).Contains("unstaged content"), "Unstaged working content is preserved");
            check(await Git("rev-list", "--count", "HEAD") == "1", "Staged amend does not add a commit");
            var beforeRace = await workbench.ReadAsync(default);
            await File.WriteAllTextAsync(Path.Combine(repository, "another.txt"), "another\n");
            await Git("add", "another.txt");
            await Rejected(() => workbench.AmendAsync(beforeRace, "Must reject changed index", default), "Concurrent index changes reject amend");
            check(await Git("rev-parse", "HEAD") == beforeRace.Status.CommitId, "Index rejection preserves HEAD");
            var valid = await workbench.ReadAsync(default);
            await Rejected(() => workbench.AmendAsync(valid, " \n ", default), "Blank commit messages are rejected");
            await File.WriteAllTextAsync(Path.Combine(repository, ".git", "MERGE_HEAD"), valid.Status.CommitId!);
            try { await Rejected(() => workbench.AmendAsync(valid, "Blocked during merge", default), "Newly started Git operations reject amend"); }
            finally { File.Delete(Path.Combine(repository, ".git", "MERGE_HEAD")); }
            var beforeHeadRace = await workbench.ReadAsync(default);
            await Git("commit", "-qm", "Concurrent commit");
            await Rejected(() => workbench.AmendAsync(beforeHeadRace, "Must reject moved HEAD", default), "Concurrent HEAD changes reject amend");
            var current = await workbench.ReadAsync(default);
            await Rejected(() => workbench.AmendAsync(current, "  \n", default), "Empty messages reject amend");
            string mergeMarker = Path.Combine(repository, ".git", "MERGE_HEAD");
            await File.WriteAllTextAsync(mergeMarker, current.Status.CommitId);
            try { await Rejected(() => workbench.AmendAsync(current, "Blocked merge", default), "An ongoing merge blocks amend"); }
            finally { File.Delete(mergeMarker); }
            var conflict = current with { Status = current.Status with { Files = new[] { new GitFileChange("tracked.txt", 'U', 'U') } } };
            await Rejected(() => workbench.AmendAsync(conflict, "Blocked conflict", default), "Conflict snapshots block amend");
            await Git("restore", "--worktree", "tracked.txt");
            await Git("init", "--bare", "-q", remote);
            await Git("remote", "add", "origin", remote);
            string branch = await Git("branch", "--show-current");
            await Git("push", "-u", "origin", branch);
            var pushed = await GitStatus.ReadAsync(repository, default);
            check(pushed.Ahead == 0 && !pushed.HasPendingPush && pushed.PushLabel == Loc.Text("GitPushCurrent"), "Pushed projects show no pending commits");
            await File.WriteAllTextAsync(Path.Combine(repository, "outgoing.txt"), "outgoing\n");
            await Git("add", "outgoing.txt");
            await Git("commit", "-qm", "Unpushed commit");
            var outgoing = await GitStatus.ReadAsync(repository, default);
            check(outgoing.State == GitState.Clean && outgoing.Ahead == 1 && outgoing.HasPendingPush && outgoing.PushLabel.Contains("1"),
                "Clean working trees still show their unpushed commit count");
            await Git("push", "origin", branch);
            string remoteHead = await Git("rev-parse", "refs/remotes/origin/" + branch);
            await workbench.AmendAsync(await workbench.ReadAsync(default), "Amended published tip", default);
            var diverged = await GitStatus.ReadAsync(repository, default);
            check(diverged.Ahead == 1 && diverged.Behind == 1 && diverged.HasPendingPush, "Amended published commits remain visible as pending push");
            check(await Git("rev-parse", "refs/remotes/origin/" + branch) == remoteHead, "Amend never pushes to the remote");
            await Git("switch", "-c", "local-only");
            var localOnly = await GitStatus.ReadAsync(repository, default);
            check(localOnly.Upstream is null && localOnly.PushLabel == Loc.Text("GitPushNotConfigured"), "Branches without upstream are not labelled pushed");
            await Git("checkout", "--detach", "-q", "HEAD");
            var detached = await workbench.ReadAsync(default);
            check(detached.AmendBlockReason is not null, "Detached HEAD disables amend");
            await Rejected(() => workbench.AmendAsync(detached, "Detached", default), "Detached HEAD cannot be amended through the workbench");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
