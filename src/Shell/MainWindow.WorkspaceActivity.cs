using System.Text.Json;
using System.Security.Cryptography;

namespace DevWorkspace;

public partial class MainWindow
{
    private WorkspaceActivity ReadWorkspaceActivity()
    {
        var repositories = _projects.Where(project => project.Repository is not null)
            .DistinctBy(project => project.Repository, StringComparer.OrdinalIgnoreCase);
        var git = repositories.Where(project => project.Git.State is GitState.Clean or GitState.Changed)
            .ToDictionary(project => project.Repository!, project => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                project.Git.Branch, project.Git.CommitId, project.Git.Ahead, project.Git.Behind, project.Git.Files
            }))), StringComparer.OrdinalIgnoreCase);
        var nuget = DashboardNuGetTargets().Where(project => _session.NuGetScans.ContainsKey(project.EntryFile!))
            .ToDictionary(project => project.EntryFile!, project => _session.NuGetScans[project.EntryFile!].Report.Findings
                .Select(FindingIdentity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var actions = _workspace is { } workspace && _session.For(workspace.Root).Activity is { } previous
            ? new Dictionary<string, string>(previous.FailedActions, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(_checksRoot, _workspace?.Root, StringComparison.OrdinalIgnoreCase) &&
            _checkReports.GetValueOrDefault(WorkspaceCheck.Actions).Rows is { } rows)
        {
            foreach (var row in rows.Where(row => row.Project?.Repository is not null && row.Link is not null))
                actions[row.Project!.Repository!] = row.Link!.AbsoluteUri;
        }
        return new(git, nuget, actions);
    }

    private static string FindingIdentity(NuGetFinding finding) =>
        JsonSerializer.Serialize(new[] { finding.Package, finding.Version, finding.Advisory });

    private void RememberWorkspaceActivity()
    {
        if (_workspace is not { } workspace)
            return;

        _session.RememberActivity(workspace.Root, ReadWorkspaceActivity());
    }

    private void UpdateWorkspaceActivity()
    {
        if (DashboardActivityRows is null)
            return;
        var changes = new List<DashboardLink>();
        var baseline = _workspace is { } workspace ? _resumeSessions.GetValueOrDefault(workspace.Root)?.Activity : null;
        if (baseline is not null)
        {
            var current = ReadWorkspaceActivity();
            var git = new Dictionary<string, string>(baseline.Git, StringComparer.OrdinalIgnoreCase);
            var nuget = new Dictionary<string, string[]>(baseline.NuGet, StringComparer.OrdinalIgnoreCase);
            var actions = new Dictionary<string, string>(baseline.FailedActions, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in _projects)
            {
                if (project.Repository is { } repository && seen.Add(repository))
                {
                    if (current.Git.TryGetValue(repository, out string? status) && git.GetValueOrDefault(repository) != status)
                        changes.Add(new(project.Name, $"Git durumu güncellendi · {project.Branch} · {project.Git.Label}", "git-workbench", project));
                    if (current.FailedActions.TryGetValue(repository, out string? run) && actions.GetValueOrDefault(repository) != run &&
                        Uri.TryCreate(run, UriKind.Absolute, out var link))
                        changes.Add(new(project.Name, "Yeni başarısız GitHub Actions çalıştırması", "external", project, link));
                }
            }
            foreach (var project in DashboardNuGetTargets())
            {
                if (current.NuGet.TryGetValue(project.EntryFile!, out var findings))
                {
                    int count = findings.Except(nuget.GetValueOrDefault(project.EntryFile!) ?? [], StringComparer.OrdinalIgnoreCase).Count();
                    if (count > 0)
                        changes.Add(new(project.Name, $"{count:N0} yeni NuGet bulgusu", "nuget-report", project));
                }
            }
        }
        DashboardActivityRows.ItemsSource = changes;
        DashboardActivityHint.Text = baseline is null ? "Karşılaştırma için mevcut durum kaydediliyor." :
            changes.Count == 0 ? "Yeni Git durumu, NuGet bulgusu veya kayıtlı Actions hatası yok." : $"{changes.Count:N0} değişiklik";
    }
}
