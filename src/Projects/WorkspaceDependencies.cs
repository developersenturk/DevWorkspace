using System.IO;
using System.Text.Json;
using System.Xml;

namespace DevWorkspace;

internal sealed record DependencyEdge(string Source, string Target, string Kind);
internal sealed record DependencyReport(DependencyEdge[] Edges, string[] Diagnostics, string[]? Manifests = null)
{
    internal string[] Reachable(string start, bool reverse)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var queue = new Queue<string>(); queue.Enqueue(start);
        while (queue.TryDequeue(out var node))
            foreach (var edge in Edges.Where(e => string.Equals(reverse ? e.Target : e.Source, node, StringComparison.OrdinalIgnoreCase)))
            {
                string next = reverse ? edge.Source : edge.Target;
                if (seen.Add(next)) queue.Enqueue(next);
            }
        seen.Remove(start);
        return seen.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

internal static class WorkspaceDependencies
{
    internal static async Task<DependencyReport> ReadAsync(IEnumerable<ProjectEntry> projects, IEnumerable<string> exclusions, CancellationToken token)
    {
        var entries = projects.ToArray();
        var report = Read(entries, exclusions, token);
        using var limit = new SemaphoreSlim(4);
        var compose = await Task.WhenAll(entries.Where(ProjectIntegrations.HasCompose).DistinctBy(p => p.Folder, StringComparer.OrdinalIgnoreCase).Select(async project =>
        {
            await limit.WaitAsync(token);
            try
            {
                string file = new[] { "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml" }.Select(name => Path.Combine(project.Folder, name)).First(File.Exists);
                var info = ProjectTools.CreateCaptureInfo("docker.exe", project.Folder, ["compose", "--file", file, "config", "--no-interpolate", "--format", "json"]);
                var (code, output, error) = await ProjectTools.CaptureAsync(info, "Docker Compose", 15, token);
                return code == 0 ? new DependencyReport(ParseCompose(file, output), []) : new DependencyReport([], [file + ": " + error.Trim()]);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException) { return new DependencyReport([], [project.Folder + ": " + ex.Message]); }
            finally { limit.Release(); }
        }));
        return new(report.Edges.Concat(compose.SelectMany(r => r.Edges)).Distinct().ToArray(), report.Diagnostics.Concat(compose.SelectMany(r => r.Diagnostics)).ToArray(), report.Manifests);
    }

    internal static DependencyEdge[] ParseCompose(string file, string json)
    {
        using var document = JsonDocument.Parse(json);
        var edges = new List<DependencyEdge>();
        if (!document.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object) return [];
        foreach (var service in services.EnumerateObject())
        {
            if (!service.Value.TryGetProperty("depends_on", out var dependencies)) continue;
            string[] targets = dependencies.ValueKind == JsonValueKind.Object ? dependencies.EnumerateObject().Select(p => p.Name).ToArray() :
                dependencies.ValueKind == JsonValueKind.Array ? dependencies.EnumerateArray().Select(p => p.GetString()).OfType<string>().ToArray() : [];
            foreach (string target in targets) edges.Add(new(file + "#" + service.Name, file + "#" + target, "Compose"));
        }
        return edges.ToArray();
    }

    internal static DependencyReport Read(IEnumerable<ProjectEntry> projects, IEnumerable<string> exclusions, CancellationToken token)
    {
        var edges = new HashSet<DependencyEdge>();
        var diagnostics = new List<string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        foreach (var file in WorkspaceTree.Files(project.Folder, exclusions.Concat(WorkspaceSearch.Exclusions), token, p => diagnostics.Add(p)))
        {
            if (!files.Add(file.FullName)) continue;
            if (files.Count > 50_000) { diagnostics.Add("File limit: 50000"); break; }
        }
        var packages = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string file in files.Where(f => Path.GetFileName(f) == "package.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("name", out var name) && name.GetString() is { } value)
                {
                    if (!packages.TryGetValue(value, out var paths)) packages[value] = paths = [];
                    paths.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { diagnostics.Add(file + ": " + ex.Message); }
        }
        foreach (string file in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".vcxproj" || Path.GetFileName(f) == "package.json"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (Path.GetFileName(file) == "package.json")
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (string section in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
                    {
                        if (!doc.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
                        foreach (var dep in deps.EnumerateObject())
                        {
                            string value = dep.Value.GetString() ?? "";
                            string? target = null;
                            if (value.StartsWith("file:", StringComparison.Ordinal) || value.StartsWith("link:", StringComparison.Ordinal))
                            {
                                target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, value[5..]));
                                if (Directory.Exists(target)) target = Path.Combine(target, "package.json");
                            }
                            else if (value.StartsWith("workspace:", StringComparison.Ordinal) && packages.TryGetValue(dep.Name, out var paths) && paths.Count == 1) target = paths[0];
                            if (target is not null) edges.Add(new(file, target, "JavaScript"));
                        }
                    }
                }
                else
                {
                    var root = ProjectTools.ReadXml(file);
                    foreach (var reference in root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
                    {
                        string? include = (string?)reference.Attribute("Include");
                        if (string.IsNullOrWhiteSpace(include)) continue;
                        if (include.Contains('$') || include.IndexOfAny(['*', '?']) >= 0) { diagnostics.Add(file + ": " + include); continue; }
                        edges.Add(new(file, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, include)), ".NET"));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or JsonException or ArgumentException or InvalidOperationException) { diagnostics.Add(file + ": " + ex.Message); }
        }
        return new(edges.OrderBy(e => e.Source).ThenBy(e => e.Target).ToArray(), diagnostics.Distinct().Take(200).ToArray(),
            files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".vcxproj" || Path.GetFileName(f) == "package.json").ToArray());
    }
}
