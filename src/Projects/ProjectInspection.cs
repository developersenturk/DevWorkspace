using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Xml;

namespace DevWorkspace;

internal sealed record InspectionRow(string Category, string Name, string Detail);

internal static class ProjectInspection
{
    internal static InspectionRow[] Relationships(ProjectEntry selected, IEnumerable<ProjectEntry> projects, CancellationToken token)
    {
        if (selected.EntryFile is not { } entry)
            return [];
        var edges = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<InspectionRow>();
        var pending = new Queue<string>(projects.Select(project => project.EntryFile).OfType<string>().Where(IsProjectEntry));
        while (pending.TryDequeue(out string? file))
        {
            token.ThrowIfCancellationRequested();
            if (!edges.TryAdd(file, []))
                continue;
            try
            {
                var (references, properties) = ProjectMovePlanner.ReadReferences(new(file));
                string[] targets = references.Select(reference => ProjectMovePlanner.Resolve(reference, file, properties))
                    .OfType<string>().Where(IsProjectEntry).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                edges[file] = targets;
                foreach (string target in targets.Where(File.Exists))
                    pending.Enqueue(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                diagnostics.Add(new(Loc.Text("LEDD1397983CA"), Path.GetFileName(file), file));
            }
        }
        var reverse = edges.SelectMany(edge => edge.Value.Select(target => (Target: target, Source: edge.Key)))
            .ToLookup(edge => edge.Target, edge => edge.Source, StringComparer.OrdinalIgnoreCase);
        return Walk(entry, file => edges.GetValueOrDefault(file) ?? [], Loc.Text("L00BFE22C2A13"))
            .Concat(Walk(entry, file => reverse[file], Loc.Text("LE6901D98598F"))).Concat(diagnostics).ToArray();
    }

    private static IEnumerable<InspectionRow> Walk(string entry, Func<string, IEnumerable<string>> neighbors, string category)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry };
        var queue = new Queue<(string File, int Depth)>();
        queue.Enqueue((entry, 0));
        while (queue.TryDequeue(out var node))
        {
            foreach (string file in neighbors(node.File).Where(seen.Add))
            {
                queue.Enqueue((file, node.Depth + 1));
                string relation = node.Depth == 0 ? Loc.Text("L02E302C78C1E") : Loc.Format("L771613407287", node.Depth + 1);
                yield return new(category, Path.GetFileName(file), $"{relation} · {file}");
            }
        }
    }

    private static bool IsProjectEntry(string path) =>
        ProjectTools.IsDotNetEntry(path) || Path.GetExtension(path).Equals(".vcxproj", StringComparison.OrdinalIgnoreCase);

    internal static async Task<InspectionRow[]> EnvironmentAsync(Workspace workspace, ProjectEntry project, CancellationToken token)
    {
        DevelopmentTools.RefreshPaths();
        var rows = new List<InspectionRow>();
        var tools = new List<(DevelopmentTool Tool, string? Requirement)>();
        if (project.EntryFile is { } entry && ProjectTools.IsDotNetEntry(entry))
            tools.Add((DevelopmentTool.DotNet, ReadSdkRequirement(project.Folder)));
        string package = Path.Combine(project.Folder, "package.json");
        if (File.Exists(package))
            tools.Add((DevelopmentTool.Node, ReadRequirement(package, "engines", "node")));
        if (File.Exists(Path.Combine(project.Folder, "pubspec.yaml")))
            tools.Add((DevelopmentTool.Flutter, null));
        if (project.Repository is not null)
            tools.Add((DevelopmentTool.Git, null));

        foreach (var (tool, requirement) in tools)
        {
            token.ThrowIfCancellationRequested();
            var definition = DevelopmentTools.Get(tool);
            if (DevelopmentTools.Find(tool) is not { } executable)
            {
                rows.Add(new(Loc.Text("LE8AF1BA5B6FF"), definition.Name, requirement ?? definition.Command));
                continue;
            }

            string status = Loc.Text("LB0BA7377089D");
            string detail = executable;
            if (tool != DevelopmentTool.Flutter)
            {
                try
                {
                    var info = ProjectTools.CreateCaptureInfo(executable, project.Folder, ["--version"]);
                    var (code, output, error) = await ProjectTools.CaptureAsync(info, definition.Name, 10, token);
                    status = code == 0 ? Loc.Text("L5AF30E9219D3") : Loc.Text("L3B5AFEB0ED88");
                    detail = (code == 0 ? output : error + output).Trim();
                }
                catch (Exception ex) when (ex is IOException or Win32Exception)
                {
                    status = Loc.Text("L3B5AFEB0ED88");
                    detail = ex.Message;
                }
            }
            rows.Add(new(status, definition.Name, requirement is null ? detail : Loc.Format("LBD1FA1EFE04B", requirement, detail)));
        }
        if (tools.Any(tool => tool.Tool == DevelopmentTool.DotNet))
        {
            var health = await ProjectHealth.CheckAsync(workspace, project, token);
            rows.AddRange(health.Findings.Where(finding => finding.Severity == "SDK")
                .Select(finding => new InspectionRow(Loc.Text("L3A1CCF19A70C"), ".NET SDK", finding.Description)));
        }
        return rows.ToArray();
    }

    private static string? ReadSdkRequirement(string folder)
    {
        for (string? current = folder; current is not null; current = Path.GetDirectoryName(current))
        {
            string path = Path.Combine(current, "global.json");
            if (File.Exists(path))
                return ReadRequirement(path, "sdk", "version");
        }
        return null;
    }

    private static string? ReadRequirement(string file, string section, string property)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(section, out var settings) &&
                settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
