using System.IO;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed record GitDestination(string Remote, string Ref, bool SetUpstream)
{
    public string Label => $"{Remote}/{Ref["refs/heads/".Length..]}";
}

internal sealed record GitWorkbenchState(GitStatus Status, string IndexSnapshot, string? InProgress,
    GitDestination? Upstream, string[] Remotes)
{
    public string? PullBlockReason =>
        Status.HeadRef is null || Status.CommitId is null ? "Pull için commit içeren bir dalda olmalısınız." :
        Upstream is null || !Remotes.Contains(Upstream.Remote, StringComparer.Ordinal) ? "Pull için dalın bir uzak dalı takip etmesi gerekir." :
        InProgress ?? (Status.State != GitState.Clean ? "Pull için çalışma ağacındaki değişiklikleri önce kaydedin." : null);
}

internal sealed record GitRemote(string Name, string? Url);

internal sealed record GitReference(string FullName, bool Current)
{
    public bool IsLocalBranch => FullName.StartsWith("refs/heads/", StringComparison.Ordinal);
    public string Name => FullName[(FullName.IndexOf('/', "refs/".Length) + 1)..];
    public string Label => $"{Name} · {(IsLocalBranch ? Current ? "Etkin dal" : "Yerel dal" :
        FullName.StartsWith("refs/tags/", StringComparison.Ordinal) ? "Etiket" : "Uzak dal")}";
}

internal sealed record GitReferenceComparison(string Before, string After, string[] Files);

internal sealed class GitWorkbench(string repository)
{
    internal async Task<GitReference[]> ReadReferencesAsync(CancellationToken token)
    {
        await VerifyRepositoryAsync(token);
        string output = await RunAsync(["for-each-ref", "--format=%(refname)%09%(HEAD)%09%(symref)",
            "refs/heads", "refs/remotes", "refs/tags"], token);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t')).Where(fields => fields.Length == 3 && fields[2].Length == 0)
            .Select(fields => new GitReference(fields[0], fields[1] == "*")).ToArray();
    }

    internal async Task SwitchBranchAsync(GitReference branch, CancellationToken token)
    {
        if (!branch.IsLocalBranch)
            throw new IOException("Geçmek için yerel bir dal seçin.");
        var state = await ReadAsync(token);
        if (state.InProgress is string operation)
            throw new IOException(operation);
        if (state.Status.State != GitState.Clean)
            throw new IOException("Dal değiştirmeden önce çalışma ağacındaki değişiklikleri kaydedin.");
        await RunAsync(["switch", "--no-guess", "--no-recurse-submodules", "--", branch.Name], token, 120);
    }

    internal async Task CreateReferenceAsync(string name, bool tag, GitReference? start, CancellationToken token)
    {
        await VerifyRepositoryAsync(token);
        await RunAsync(["check-ref-format", (tag ? "refs/tags/" : "refs/heads/") + name], token);
        string commit = await ResolveCommitAsync(start?.FullName ?? "HEAD", token);
        if (tag)
            await RunAsync(["-c", "tag.gpgSign=false", "tag", "--", name, commit], token);
        else
            await RunAsync(["branch", "--no-track", "--", name, commit], token);
    }

    private async Task<string> ResolveCommitAsync(string reference, CancellationToken token) =>
        (await RunAsync(["rev-parse", "--verify", "--end-of-options", reference + "^{commit}"], token)).Trim();

    internal async Task<GitReferenceComparison> CompareReferencesAsync(GitReference before, GitReference after, CancellationToken token)
    {
        await VerifyRepositoryAsync(token);
        string left = await ResolveCommitAsync(before.FullName, token);
        string right = await ResolveCommitAsync(after.FullName, token);
        string files = await RunAsync(["diff", "--name-only", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", left, right, "--"], token);
        return new(left, right, files.Split('\0', StringSplitOptions.RemoveEmptyEntries));
    }

    internal async Task<GitDiffPreview> CompareReferenceFileAsync(GitReferenceComparison comparison, string file, CancellationToken token)
    {
        await VerifyRepositoryAsync(token);
        string patch = await RunAsync(["--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-color",
            "--no-renames", "--unified=3", comparison.Before, comparison.After, "--", file], token);
        return GitDiffPreview.FromPatch(patch);
    }

    internal async Task<string[]> ReadRemoteNamesAsync(CancellationToken token)
    {
        await VerifyRepositoryAsync(token);
        return (await RunAsync(["remote"], token)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    public async Task<GitWorkbenchState> ReadAsync(CancellationToken token)
    {
        await VerifyRepositoryAsync(token);

        string before = await ReadIndexAsync(token);
        GitStatus status = await GitStatus.ReadAsync(repository, token);
        if (status.State is not (GitState.Clean or GitState.Changed))
            throw new IOException(status.Detail);

        Task<string> currentIndex = ReadIndexAsync(token);
        Task<string?> inProgress = ReadOperationAsync(token);
        Task<GitDestination?> upstream = ReadUpstreamAsync(status.HeadRef, token);
        await Task.WhenAll(currentIndex, inProgress, upstream);
        if (before != await currentIndex)
            throw new IOException("Hazırlanan dosyalar okunurken değişti. Listeyi yenileyin.");
        return new(status, before, await inProgress, await upstream, status.Remotes?.ToArray() ?? []);
    }

    public async Task StageAsync(GitStatus expected, GitFileChange[] files, CancellationToken token)
    {
        if (files.Length == 0)
            return;

        await VerifyHeadAsync(expected, token);
        var paths = files.SelectMany(file => file.WorkingTree == 'R' && file.OriginalPath is string original
            ? new[] { file.Path, original } : [file.Path]).Distinct(StringComparer.Ordinal);
        string pathList = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(pathList, string.Join('\0', paths) + '\0', token);
            await RunAsync(["--literal-pathspecs", "-c", "add.ignoreErrors=false", "add", "-A",
                $"--pathspec-from-file={pathList}", "--pathspec-file-nul"], token, 120);
        }
        finally
        {
            File.Delete(pathList);
        }
    }

    public async Task UnstageAsync(GitStatus expected, GitFileChange file, CancellationToken token)
    {
        await VerifyHeadAsync(expected, token);
        var paths = file.Index == 'R' && file.OriginalPath is string original ? new[] { file.Path, original } : [file.Path];
        await RunAsync(["--literal-pathspecs", "reset", "--quiet", "--", .. paths], token);
    }

    public async Task CommitAsync(GitWorkbenchState expected, string message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrEmpty(expected.IndexSnapshot))
            throw new IOException("Commit mesajı yazın ve en az bir dosya hazırlayın.");
        if (expected.Status.HeadRef is null)
            throw new IOException("Commit için bir dala geçin.");
        if (await ReadOperationAsync(token) is string operation)
            throw new IOException(operation);
        await VerifyHeadAsync(expected.Status, token);
        if (!string.Equals(await ReadIndexAsync(token), expected.IndexSnapshot, StringComparison.Ordinal))
            throw new IOException("Hazırlanan değişiklikler başka bir işlem tarafından güncellendi. Listeyi inceleyip yeniden deneyin.");

        await RunAsync(["commit", "-m", message], token, 120);
    }

    public async Task PushAsync(GitWorkbenchState expected, GitDestination destination, CancellationToken token)
    {
        if (expected.Status.HeadRef is not string head || expected.Status.CommitId is not string commit)
            throw new IOException("Push için dalda en az bir commit olmalı.");
        await VerifyHeadAsync(expected.Status, token);
        if (await ReadUpstreamAsync(head, token) != expected.Upstream)
            throw new IOException("Takip edilen dal değişti. Güncel hedefi inceleyip yeniden deneyin.");
        if (destination.SetUpstream)
        {
            if (expected.Upstream is not null || destination.Ref != head ||
                !expected.Remotes.Contains(destination.Remote, StringComparer.Ordinal))
                throw new IOException("İlk push için geçerli bir uzak depo seçin.");
        }
        else if (destination != expected.Upstream)
            throw new IOException("Takip edilen uzak dal seçili değil.");

        string[] remotes = (await RunAsync(["remote"], token)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!remotes.Contains(destination.Remote, StringComparer.Ordinal))
            throw new IOException("Uzak depo artık yapılandırılmamış.");

        string[] tracking = destination.SetUpstream ? ["--set-upstream"] : [];
        string source = destination.SetUpstream ? head : commit;
        await RunAsync(["-c", $"remote.{destination.Remote}.mirror=false", "push", "--porcelain",
            "--no-follow-tags", "--recurse-submodules=no", .. tracking, "--", destination.Remote, $"{source}:{destination.Ref}"], token, 120);
    }

    public async Task FetchAsync(string remote, CancellationToken token)
    {
        await VerifyRepositoryAsync(token);
        if ((await ReadRemoteAsync(remote, token)).Url is null)
            throw new IOException("Seçilen uzak deponun adresi bulunamadı.");
        await RunAsync(["fetch", "--no-recurse-submodules", "--", remote], token, 120);
    }

    public async Task PullAsync(GitWorkbenchState expected, CancellationToken token)
    {
        if (expected.PullBlockReason is string blocked)
            throw new IOException(blocked);

        GitWorkbenchState current = await ReadAsync(token);
        if (current.Status.HeadRef != expected.Status.HeadRef || current.Status.CommitId != expected.Status.CommitId ||
            current.Upstream != expected.Upstream)
            throw new IOException("Etkin dal, commit veya takip hedefi değişti. Güncel durumu inceleyip yeniden deneyin.");
        if (current.PullBlockReason is string reason)
            throw new IOException(reason);

        GitDestination target = current.Upstream!;
        await RunAsync(["pull", "--ff-only", "--no-rebase", "--no-autostash", "--no-recurse-submodules",
            "--", target.Remote, target.Ref], token, 120);
    }

    public async Task<GitRemote> ReadRemoteAsync(string remote, CancellationToken token)
    {
        var (code, output, _) = await GitProjectOperations.RunAsync(repository, ["remote", "get-url", "--", remote], token);
        if (code != 0 || string.IsNullOrWhiteSpace(output))
            return new(remote, null);

        string url = Regex.Replace(output.TrimEnd('\r', '\n'), @"^(https?://[^?#]*)[?#].*$", "$1", RegexOptions.IgnoreCase);
        return new(remote, ErrorText(url));
    }

    private async Task VerifyHeadAsync(GitStatus expected, CancellationToken token)
    {
        await VerifyRepositoryAsync(token);
        var (branchCode, head, branchError) = await GitProjectOperations.RunAsync(repository, ["symbolic-ref", "--quiet", "HEAD"], token);
        var (commitCode, commit, commitError) = await GitProjectOperations.RunAsync(repository, ["rev-parse", "--verify", "--quiet", "HEAD"], token);
        if (branchCode > 1 || commitCode > 1)
            throw new IOException(ErrorText(branchError + "\n" + commitError));
        if ((branchCode == 0 ? head.Trim() : null) != expected.HeadRef ||
            (commitCode == 0 ? commit.Trim() : null) != expected.CommitId)
            throw new IOException("Etkin dal veya commit değişti. Güncel listeyi inceleyip yeniden deneyin.");
    }

    private async Task VerifyRepositoryAsync(CancellationToken token)
    {
        string top = (await RunAsync(["rev-parse", "--show-toplevel"], token)).TrimEnd('\r', '\n');
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(top)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Git deposunun kök klasörü değişti.");
    }

    private Task<string> ReadIndexAsync(CancellationToken token) =>
        RunAsync(["-c", "core.fsmonitor=false", "diff", "--cached", "--raw", "--no-renames", "--no-abbrev",
            "--no-ext-diff", "--no-textconv", "--ignore-submodules=none", "-z", "--"], token);

    private async Task<string?> ReadOperationAsync(CancellationToken token)
    {
        string gitDirectory = (await RunAsync(["rev-parse", "--absolute-git-dir"], token)).TrimEnd('\r', '\n');
        string[] markers = ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-apply", "rebase-merge", "sequencer"];
        return markers.Any(marker => Path.Exists(Path.Combine(gitDirectory, marker)))
            ? "Depoda devam eden bir Git işlemi var. Önce bu işlemi tamamlayın." : null;
    }

    internal async Task<GitDestination?> ReadUpstreamAsync(string? head, CancellationToken token)
    {
        if (head is null)
            return null;

        string branch = head["refs/heads/".Length..];
        Task<string[]> remote = ReadConfigAsync($"branch.{branch}.remote", token);
        Task<string[]> merge = ReadConfigAsync($"branch.{branch}.merge", token);
        await Task.WhenAll(remote, merge);
        string[] values = await merge;
        string? name = (await remote).LastOrDefault();
        if (values.Length == 0)
            return null;
        if (values.Length != 1 || name is null || !values[0].StartsWith("refs/heads/", StringComparison.Ordinal))
            return null;
        return new(name, values[0], false);
    }

    private async Task<string[]> ReadConfigAsync(string key, CancellationToken token)
    {
        var (code, output, error) = await GitProjectOperations.RunAsync(repository, ["config", "--null", "--get-all", key], token);
        return code switch
        {
            0 => output.Split('\0', StringSplitOptions.RemoveEmptyEntries),
            1 when string.IsNullOrWhiteSpace(error) => [],
            _ => throw new IOException(ErrorText(error))
        };
    }

    private async Task<string> RunAsync(string[] args, CancellationToken token, int timeoutSeconds = 12)
    {
        var (code, output, error) = await GitProjectOperations.RunAsync(repository, args, token, timeoutSeconds);
        if (code != 0)
            throw new IOException(ErrorText(string.IsNullOrWhiteSpace(error) ? output : error));
        return output;
    }

    private static string ErrorText(string error)
    {
        string message = Regex.Replace(error.Trim(), @"(https?://)[^\s/@]+@", "$1", RegexOptions.IgnoreCase);
        return message.Length == 0 ? "Git işlemi tamamlanamadı." : message;
    }
}
