using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal static class EnvironmentChecks
{
    internal static async Task<InspectionRow[]> ReadAsync(ProjectEntry project, CancellationToken token)
    {
        var rows = new List<InspectionRow>();
        string? nodeRequirement = null;
        foreach (string name in new[] { ".nvmrc", ".node-version" })
        {
            string path = Path.Combine(project.Folder, name);
            if (File.Exists(path)) { nodeRequirement = File.ReadAllText(path).Trim(); break; }
        }
        if (nodeRequirement is null)
        {
            string package = Path.Combine(project.Folder, "package.json");
            if (File.Exists(package))
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(package));
                if (json.RootElement.TryGetProperty("engines", out var engines) && engines.TryGetProperty("node", out var node)) nodeRequirement = node.GetString();
            }
        }
        if (nodeRequirement is not null)
        {
            try
            {
                var (code, output, error) = await ProjectTools.CaptureAsync(ProjectTools.CreateCaptureInfo("node.exe", project.Folder, ["--version"]), "Node.js", 10, token);
                bool? matches = code == 0 ? MatchesNode(output.Trim(), nodeRequirement) : false;
                rows.Add(new(WorkbenchText.T(matches == true ? "Ready" : matches == false ? "Attention" : "ManualCheck"), "Node.js", $"{nodeRequirement} · {(code == 0 ? output.Trim() : error.Trim())}"));
            }
            catch (Exception ex) when (ex is IOException or Win32Exception) { rows.Add(new(WorkbenchText.T("Attention"), "Node.js", ex.Message)); }
        }
        foreach (string example in new[] { ".env.example", ".env.sample", ".env.template" })
        {
            string path = Path.Combine(project.Folder, example);
            if (!File.Exists(path)) continue;
            var required = EnvKeys(File.ReadAllLines(path));
            string env = Path.Combine(project.Folder, ".env");
            string[] actual = File.Exists(env) ? File.ReadAllLines(env) : [];
            var present = EnvKeys(actual);
            string[] missing = required.Except(present, StringComparer.Ordinal).ToArray();
            rows.Add(new(WorkbenchText.T(missing.Length == 0 && File.Exists(env) ? "Ready" : "Attention"), ".env", !File.Exists(env) ? WorkbenchText.T("EnvMissing") : missing.Length == 0 ? WorkbenchText.T("EnvReady") : WorkbenchText.T("EnvKeys") + " " + string.Join(", ", missing)));
            break;
        }
        if (ProjectIntegrations.HasCompose(project))
        {
            try
            {
                var (code, output, error) = await ProjectTools.CaptureAsync(ProjectTools.CreateCaptureInfo("docker.exe", project.Folder, ["compose", "ps", "--all", "--format", "json"]), "Docker Compose", 15, token);
                if (code != 0) rows.Add(new(WorkbenchText.T("Attention"), "Docker Compose", error.Trim()));
                else
                {
                    var services = ParseCompose(output);
                    rows.AddRange(services.Select(s => new InspectionRow(WorkbenchText.T(s.Status == "running" ? "Ready" : "Attention"), s.Name, s.Status)));
                    if (services.Length == 0) rows.Add(new(WorkbenchText.T("Attention"), "Docker Compose", WorkbenchText.T("ComposeStopped")));
                }
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or System.Text.Json.JsonException) { rows.Add(new(WorkbenchText.T("Attention"), "Docker Compose", ex.Message)); }
        }
        return rows.ToArray();
    }

    internal static HashSet<string> EnvKeys(IEnumerable<string> lines) => lines.Select(l => Regex.Match(l, @"^\s*(?:export\s+)?(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*="))
        .Where(m => m.Success).Select(m => m.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);

    internal static bool? MatchesNode(string installed, string requirement)
    {
        string version = installed.Trim().TrimStart('v');
        if (!Version.TryParse(version, out var actual)) return null;
        bool unknown = false;
        foreach (string alternative in requirement.Split("||", StringSplitOptions.TrimEntries))
        {
            bool supported = true, matches = true;
            string[] parts = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            foreach (string part in parts)
            {
                if (part is "*" or "x" or "X") continue;
                var match = Regex.Match(part, @"^(?<op>\^|~|>=|<=|>|<|=)?v?(?<major>\d+)(?:\.(?<minor>\d+|x|\*))?(?:\.(?<patch>\d+|x|\*))?$", RegexOptions.IgnoreCase);
                if (!match.Success) { supported = false; break; }
                int major = int.Parse(match.Groups["major"].Value);
                bool hasMinor = int.TryParse(match.Groups["minor"].Value, out int minor), hasPatch = int.TryParse(match.Groups["patch"].Value, out int patch);
                var expected = new Version(major, minor, patch);
                var partialUpper = hasMinor ? new Version(major, minor + 1, 0) : new Version(major + 1, 0, 0);
                var caretUpper = major > 0 || !hasMinor ? new Version(major + 1, 0, 0) : minor > 0 || !hasPatch ? new Version(0, minor + 1, 0) : new Version(0, 0, patch + 1);
                matches &= match.Groups["op"].Value switch
                {
                    ">=" => actual >= expected, "<=" => hasPatch ? actual <= expected : actual < partialUpper,
                    ">" => hasPatch ? actual > expected : actual >= partialUpper, "<" => actual < expected,
                    "^" => actual >= expected && actual < caretUpper,
                    "~" => actual >= expected && actual.Major == major && (!hasMinor || actual.Minor == minor),
                    _ => actual.Major == major && (!hasMinor || actual.Minor == minor) && (!hasPatch || actual.Build == patch)
                };
            }
            if (!supported) { unknown = true; continue; }
            if (matches) return true;
        }
        return unknown ? null : false;
    }

    private static ComposeService[] ParseCompose(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var doc = System.Text.Json.JsonDocument.Parse(output.TrimStart().StartsWith('[') ? output : "[" + string.Join(",", output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) + "]");
        return doc.RootElement.EnumerateArray().Select(e => new ComposeService(e.GetProperty("Service").GetString() ?? "", e.GetProperty("State").GetString() ?? "")).ToArray();
    }
}
