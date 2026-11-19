namespace DevWorkspace;

internal sealed record RepositorySummary(string Project, string Repository, string Branch, string Upstream, int? Ahead, int? Behind, int? Changes, int? Stashes, string Detail);

internal static class WorkspaceGitSummary
{
    internal static async Task<RepositorySummary[]> ReadAsync(IEnumerable<ProjectEntry> projects, CancellationToken token)
    {
        using var limit = new SemaphoreSlim(4);
        var tasks = projects.Where(p => p.Repository is not null).DistinctBy(p => p.Repository, StringComparer.OrdinalIgnoreCase).Select(async project =>
        {
            await limit.WaitAsync(token);
            try
            {
                var status = await GitStatus.ReadAsync(project.Repository!, token);
                var (code, output, error) = await GitProjectOperations.RunAsync(project.Repository!, ["stash", "list", "--format=%gd"], token);
                return new RepositorySummary(project.Name, project.Repository!, status.Branch ?? "", status.Upstream ?? "—", status.Ahead,
                    status.Behind, status.Changes, code == 0 ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : null,
                    status.Detail + (code == 0 ? "" : " " + error.Trim()));
            }
            finally { limit.Release(); }
        });
        return (await Task.WhenAll(tasks)).OrderByDescending(r => r.Ahead.GetValueOrDefault() + r.Behind.GetValueOrDefault() + r.Changes.GetValueOrDefault() + r.Stashes.GetValueOrDefault()).ToArray();
    }
}
