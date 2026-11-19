using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DevWorkspace;

internal sealed record SharedLaunchStep(string Project, string? Preset, string Action = "default");
internal sealed record SharedLaunchProfile(int SchemaVersion, string Name, SharedLaunchStep[] Steps);

internal static class WorkspaceSharing
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static bool PortablePath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) && !path.Contains(':') && !path.Split('/', '\\').Contains("..");

    private static string Signature(ProjectCommand command, string root) =>
        command.Executable.ToLowerInvariant() + " " + command.Arguments.Replace(
            Path.TrimEndingDirectorySeparator(root), "${workspace}", StringComparison.OrdinalIgnoreCase);

    internal static string ExportProfile(LaunchProfile profile, IReadOnlyList<ProjectEntry> projects, string root)
    {
        var steps = profile.Steps.Select(step =>
        {
            if (step.Action is not ("default" or "terminal") || (step.Action == "terminal" && step.Command is not null))
                throw new InvalidDataException(Loc.Text("SharePresetOnly"));
            var project = projects.FirstOrDefault(p => p.RelativePath.Equals(step.Project, StringComparison.OrdinalIgnoreCase));
            if (project is null || project.LinkPath is not null || !PortablePath(step.Project))
                throw new InvalidDataException(Loc.Text("SharePortableOnly"));
            string? preset = null;
            if (step.Command is { } command)
            {
                if (!ProjectTools.Presets(project).Any(p => p.Executable == command.Executable && p.Arguments == command.Arguments))
                    throw new InvalidDataException(Loc.Text("SharePresetOnly"));
                preset = Signature(command, root);
                if (System.Text.RegularExpressions.Regex.IsMatch(preset, @"[A-Za-z]:[\\/]|\\\\"))
                    throw new InvalidDataException(Loc.Text("SharePortableOnly"));
            }
            return new SharedLaunchStep(step.Project.Replace('\\', '/'), preset, step.Action);
        }).ToArray();
        return JsonSerializer.Serialize(new SharedLaunchProfile(1, profile.Name, steps), Json);
    }

    internal static LaunchProfile ImportProfile(string json, IReadOnlyList<ProjectEntry> projects, string root)
    {
        if (json.Length > 1024 * 1024) throw new InvalidDataException(Loc.Text("ShareInvalid"));
        var shared = JsonSerializer.Deserialize<SharedLaunchProfile>(json, Json);
        if (shared is null || shared.SchemaVersion != 1 || string.IsNullOrWhiteSpace(shared.Name) || shared.Name.Length > 120 ||
            shared.Steps is not { Length: > 0 and <= 100 })
            throw new InvalidDataException(Loc.Text("ShareInvalid"));
        var steps = shared.Steps.Select(step =>
        {
            if (step is null || !PortablePath(step.Project)) throw new InvalidDataException(Loc.Text("ShareInvalid"));
            if (step.Action is not ("default" or "terminal") || (step.Action == "terminal" && step.Preset is not null))
                throw new InvalidDataException(Loc.Text("ShareInvalid"));
            var project = projects.FirstOrDefault(p => p.LinkPath is null &&
                p.RelativePath.Replace('\\', '/').Equals(step.Project.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(Loc.Format("ShareMissingProject", step.Project));
            var command = step.Preset is null ? null : ProjectTools.Presets(project)
                .FirstOrDefault(p => Signature(p, root) == step.Preset)
                ?? throw new InvalidDataException(Loc.Text("SharePresetOnly"));
            return new LaunchStep(project.RelativePath, command, step.Action);
        }).ToArray();
        return new(shared.Name.Trim(), steps);
    }

    internal static string Report(IReadOnlyList<ProjectEntry> projects, bool includeNames)
    {
        string E(string text) => WebUtility.HtmlEncode(text);
        var html = new StringBuilder("<!doctype html><html lang=\"" + Loc.Language + "\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>DevWorkspace</title><style>body{font:16px/1.6 system-ui;max-width:1000px;margin:48px auto;padding:0 24px;color:#17213a}h1{font-size:36px}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:12px;border-bottom:1px solid #dce2eb}caption{text-align:left;margin:24px 0} @media print{body{margin:0}} </style><h1>DevWorkspace</h1>");
        html.Append("<p>").Append(E(Loc.Text("ReportSnapshot"))).Append(" · ").Append(DateTimeOffset.UtcNow.ToString("u")).Append("</p>");
        html.Append("<p>").Append(E(Loc.Format("ReportSummary", projects.Count,
            projects.Count(p => p.Repository is not null), projects.Count(p => p.Git.State == GitState.Changed)))).Append("</p>");
        html.Append("<table><caption>").Append(E(Loc.Text("ReportPrivacy"))).Append("</caption><thead><tr>");
        foreach (string key in new[] { "ReportProject", "ReportType", "ReportGit", "ReportChanges" }) html.Append("<th scope=\"col\">").Append(E(Loc.Text(key))).Append("</th>");
        html.Append("</tr></thead><tbody>");
        for (int i = 0; i < projects.Count; i++)
        {
            var p = projects[i];
            html.Append("<tr><td>").Append(E(includeNames ? p.Name : $"{Loc.Text("ReportProject")} {i + 1}"))
                .Append("</td><td>").Append(E(p.Kind)).Append("</td><td>").Append(E(p.Git.Label))
                .Append("</td><td>").Append(p.Git.Changes?.ToString() ?? "—").Append("</td></tr>");
        }
        return html.Append("</tbody></table></html>").ToString();
    }

    internal static void WriteAtomic(string path, string content)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class UsageMetrics
{
    internal static readonly HashSet<string> Events = ["app_open", "workspace_open", "workspace_failed", "profile_run", "profile_import", "report_export", "bug_report"];
    internal static void Record(SessionState state, string name)
    {
        if (!state.UsageMetricsEnabled || !Events.Contains(name)) return;
        state.UsageCounters.TryGetValue(name, out long count);
        state.UsageCounters[name] = count < long.MaxValue ? count + 1 : count;
    }
}
