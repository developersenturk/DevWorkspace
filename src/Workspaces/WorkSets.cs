using System.IO;

namespace DevWorkspace;

internal sealed record WorkSet(Guid Id, string Name, string Notes, string? Profile, WorkSetProject[] Projects, DateTime UpdatedUtc);
internal sealed record WorkSetProject(string Project, string? Repository, string? Branch, string? Commit, ResumeNote? Note);

internal static class WorkSets
{
    internal static async Task<WorkSet> CaptureAsync(Guid id, string name, string notes, string? profile,
        ProjectEntry[] projects, WorkspaceSession session, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || notes.Length > 12000 || projects.Length is 0 or > 100)
            throw new InvalidDataException(WorkbenchText.T("WorkSetRequired"));
        var snapshots = new List<WorkSetProject>();
        var repositories = new Dictionary<string, (string? Branch, string? Commit)>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects.DistinctBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            string? branch = null, commit = null;
            if (project.Repository is { } repository)
            {
                if (!repositories.TryGetValue(repository, out var state))
                {
                    var head = await GitProjectOperations.RunAsync(repository, ["rev-parse", "--verify", "HEAD"], token);
                    if (head.Code != 0) throw new IOException(WorkbenchText.T("WorkSetNoCommit") + " " + project.Name);
                    var symbolic = await GitProjectOperations.RunAsync(repository, ["symbolic-ref", "--quiet", "--short", "HEAD"], token);
                    state = (symbolic.Code == 0 ? symbolic.Output.Trim() : null, head.Output.Trim());
                    repositories[repository] = state;
                }
                (branch, commit) = state;
            }
            var note = session.ResumeNotes?.FirstOrDefault(p => p.Key.Equals(project.RelativePath, StringComparison.OrdinalIgnoreCase)).Value;
            snapshots.Add(new(project.RelativePath, project.Repository, branch, commit, note));
        }
        return new(id, name.Trim(), notes.Trim(), profile, snapshots.ToArray(), DateTime.UtcNow);
    }

    private sealed record Switch(string Repository, string? Branch, string Commit, string? PreviousBranch, string PreviousCommit);

    internal static async Task RestoreBranchesAsync(WorkSet work, IReadOnlyList<ProjectEntry> projects, CancellationToken token)
    {
        if (work.Projects is not { Length: > 0 and <= 100 }) throw new InvalidDataException(WorkbenchText.T("WorkSetRequired"));
        var plan = new List<Switch>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in work.Projects)
        {
            token.ThrowIfCancellationRequested();
            var project = projects.FirstOrDefault(p => p.RelativePath.Equals(saved.Project, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException(string.Format(WorkbenchText.T("MissingWorkflowProject"), saved.Project));
            if (!Directory.Exists(project.Folder) || !string.Equals(project.Repository, saved.Repository, StringComparison.OrdinalIgnoreCase))
                throw new IOException(WorkbenchText.T("WorkSetMoved") + " " + project.Name);
            if (project.Repository is not { } repository || !seen.Add(repository)) continue;
            if (saved.Commit is null || !System.Text.RegularExpressions.Regex.IsMatch(saved.Commit, "^[a-fA-F0-9]{40,64}$"))
                throw new InvalidDataException(WorkbenchText.T("WorkSetRequired"));
            if (ArtifactCleanup.IsCleaning(project.Folder)) throw new IOException(WorkbenchText.T("CleanupRunning"));
            var current = await GitStatus.ReadAsync(repository, token);
            if (current.State is not (GitState.Clean or GitState.Changed)) throw new IOException(current.Detail);
            var symbolic = await GitProjectOperations.RunAsync(repository, ["symbolic-ref", "--quiet", "--short", "HEAD"], token);
            string? previousBranch = symbolic.Code == 0 ? symbolic.Output.Trim() : null;
            var head = await GitProjectOperations.RunAsync(repository, ["rev-parse", "--verify", "HEAD"], token);
            if (head.Code != 0) throw new IOException(head.Error);
            string previousCommit = head.Output.Trim();
            if (saved.Branch is not null)
            {
                var exists = await GitProjectOperations.RunAsync(repository, ["show-ref", "--verify", "--quiet", "refs/heads/" + saved.Branch], token);
                if (exists.Code != 0) throw new IOException(WorkbenchText.T("WorkSetBranchMissing") + " " + saved.Branch);
                var trees = await GitProjectOperations.RunAsync(repository, ["worktree", "list", "--porcelain"], token);
                string? path = null;
                foreach (string line in trees.Output.Split('\n'))
                {
                    if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..].Trim();
                    if (line.Trim() == "branch refs/heads/" + saved.Branch && path is not null &&
                        !Path.GetFullPath(path).Equals(Path.GetFullPath(repository), StringComparison.OrdinalIgnoreCase))
                        throw new IOException(WorkbenchText.T("WorkSetBranchInUse") + " " + path);
                }
            }
            else
            {
                var exists = await GitProjectOperations.RunAsync(repository, ["cat-file", "-e", saved.Commit + "^{commit}"], token);
                if (exists.Code != 0) throw new IOException(WorkbenchText.T("WorkSetBranchMissing") + " " + saved.Commit);
            }
            bool same = saved.Branch is not null ? saved.Branch == previousBranch : previousBranch is null && saved.Commit == previousCommit;
            if (same) continue;
            if (current.State != GitState.Clean) throw new IOException(WorkbenchText.T("WorkSetDirty") + " " + project.Name);
            plan.Add(new(repository, saved.Branch, saved.Commit, previousBranch, previousCommit));
        }
        var applied = new List<Switch>();
        try
        {
            foreach (var item in plan)
            {
                var current = await GitStatus.ReadAsync(item.Repository, token);
                if (current.State != GitState.Clean) throw new IOException(WorkbenchText.T("WorkSetDirty") + " " + item.Repository);
                await SwitchAsync(item.Repository, item.Branch, item.Commit, token); applied.Add(item);
            }
        }
        catch (Exception error)
        {
            var unresolved = new List<string>();
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if ((await GitStatus.ReadAsync(item.Repository, CancellationToken.None)).State != GitState.Clean)
                    { unresolved.Add(item.Repository); continue; }
                    await SwitchAsync(item.Repository, item.PreviousBranch, item.PreviousCommit, CancellationToken.None);
                }
                catch { unresolved.Add(item.Repository); }
            }
            if (unresolved.Count > 0) throw new IOException(error.Message + Environment.NewLine + WorkbenchText.T("WorkSetPartial") +
                Environment.NewLine + string.Join(Environment.NewLine, unresolved), error);
            throw;
        }
    }

    private static async Task SwitchAsync(string repository, string? branch, string commit, CancellationToken token)
    {
        var result = await GitProjectOperations.RunAsync(repository, branch is null
            ? ["switch", "--no-overwrite-ignore", "--no-recurse-submodules", "--detach", commit]
            : ["switch", "--no-overwrite-ignore", "--no-guess", "--no-recurse-submodules", "--", branch], token);
        if (result.Code != 0) throw new IOException(result.Error + result.Output);
    }
}
