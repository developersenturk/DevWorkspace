using System.IO;
using System.Text.Json;

namespace DevWorkspace;

internal sealed record ImpactTarget(string Name, string Manifest, string Relationship, string Folder, ProjectCommand? Test)
{
    public string TestCommand => Test?.Display ?? "—";
}
internal sealed record ImpactReport(ImpactTarget[] Targets, string[] Seeds, string[] Diagnostics);

internal static class ImpactAnalysis
{
    internal static ImpactReport Read(ProjectEntry selected, ProjectEntry[] projects, IEnumerable<string> exclusions, bool changedOnly, CancellationToken token)
    {
        var dependencies = WorkspaceDependencies.Read(projects, exclusions, token);
        return Build(selected, dependencies, changedOnly, token);
    }

    internal static ImpactReport Build(ProjectEntry selected, DependencyReport graph, bool changedOnly, CancellationToken token)
    {
        string[] manifests = (graph.Manifests ?? []).Concat(graph.Edges.SelectMany(e => new[] { e.Source, e.Target }))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string[] seeds;
        var changed = selected.Repository is { } repository ? (selected.Git.Files ?? []).SelectMany(f =>
            new[] { f.Path, f.OriginalPath }.OfType<string>().Select(p => Path.GetFullPath(p, repository)))
            .Where(p => Within(p, selected.Folder)).ToArray() : [];
        if (changedOnly && changed.Length > 0)
        {
            seeds = changed.SelectMany(path =>
            {
                // Shared MSBuild/package configuration can affect every manifest below its folder.
                string name = Path.GetFileName(path);
                if (name is "Directory.Build.props" or "Directory.Build.targets" or "Directory.Packages.props" or "global.json" or "pnpm-workspace.yaml")
                    return manifests.Where(m => Within(m, Path.GetDirectoryName(path)!));
                var owner = manifests.Where(m => Within(path, Path.GetDirectoryName(m)!)).OrderByDescending(m => Path.GetDirectoryName(m)!.Length).FirstOrDefault();
                return owner is null ? Array.Empty<string>() : [owner];
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        else seeds = selected.EntryFile is { } entry && manifests.Contains(entry, StringComparer.OrdinalIgnoreCase)
            ? [entry] : manifests.Where(m => Within(m, selected.Folder)).ToArray();
        var seen = seeds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string seed in seeds)
        {
            token.ThrowIfCancellationRequested();
            direct.UnionWith(graph.Edges.Where(e => e.Target.Equals(seed, StringComparison.OrdinalIgnoreCase)).Select(e => e.Source));
            seen.UnionWith(graph.Reachable(seed, reverse: true));
        }
        var targets = seen.Order(StringComparer.OrdinalIgnoreCase).Select(file =>
        {
            token.ThrowIfCancellationRequested();
            string folder = Path.GetDirectoryName(file)!;
            ProjectCommand? test = null;
            if (File.Exists(file) && Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" && TestProjectDetection.IsTestProject(file))
                test = new(WorkbenchText.T("RunRelevantTests"), "dotnet.exe", "test \"" + file + "\" -c Debug");
            if (File.Exists(file) && Path.GetFileName(file) == "package.json")
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.TryGetProperty("test", out _))
                        test = new(WorkbenchText.T("RunRelevantTests"), "cmd.exe", File.Exists(Path.Combine(folder, "pnpm-lock.yaml")) ? "/d /c pnpm.cmd test" :
                            File.Exists(Path.Combine(folder, "yarn.lock")) ? "/d /c yarn.cmd test" : "/d /c npm.cmd test");
                }
                catch (JsonException) { }
            }
            return new ImpactTarget(Path.GetFileNameWithoutExtension(file), file,
                WorkbenchText.T(seeds.Contains(file, StringComparer.OrdinalIgnoreCase) ? "ImpactSource" : direct.Contains(file) ? "ImpactDirect" : "ImpactTransitive"), folder, test);
        }).ToArray();
        return new(targets, seeds, graph.Diagnostics);
    }
}
