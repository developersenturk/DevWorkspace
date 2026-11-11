using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DevWorkspace;

internal sealed record PackageSecurityFinding(string Name, string Version, string Severity, string Advisory);
internal sealed record PackageSecurityResult(PackageSecurityFinding[] Findings, bool HasPackages)
{
    internal HashSet<(string Name, string Version)> Packages { get; } = [];
    internal Dictionary<string, HashSet<string>> FixedVersions { get; } = new(StringComparer.Ordinal);
}

internal static class PackageSecurity
{
    private static readonly string[] JavaScriptLockfiles = ["package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lock"];

    internal static bool IsSupported(ProjectEntry project) =>
        Path.GetFileName(project.EntryFile) is { } file &&
        (file.Equals("package.json", StringComparison.OrdinalIgnoreCase) || file.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase));

    internal static string[] FindLockfiles(Workspace workspace, ProjectEntry project)
    {
        string[] names = Path.GetFileName(project.EntryFile)?.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase) == true
            ? ["pubspec.lock"] : JavaScriptLockfiles;
        string boundary = project.Repository ?? (project.LinkPath is null ? workspace.Root : project.Folder);
        for (string? folder = project.Folder; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var files = names.Select(name => Path.Combine(folder, name)).Where(File.Exists).ToArray();
            if (files.Length > 0)
                return files;
            if (folder.Equals(boundary, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return [];
    }

    internal static async Task<PackageSecurityResult> ScanAsync(string lockfile, CancellationToken token)
    {
        var info = ProjectTools.CreateCaptureInfo("osv-scanner.exe", Environment.SystemDirectory,
            ["scan", "source", "--no-resolve", "--all-packages", "--format", "json", "-L", lockfile]);
        var (code, output, error) = await ProjectTools.CaptureAsync(info, "Paket güvenliği taraması", 120, token);
        if (code == 128)
            return new([], false);
        if (code is not (0 or 1))
        {
            string detail = error.Trim();
            throw new IOException($"OSV-Scanner taramayı tamamlayamadı (kod {code}). {detail[..Math.Min(detail.Length, 450)]}");
        }
        var result = Parse(output);
        if (code == 1 && result.Findings.Length == 0)
            throw new IOException("OSV-Scanner sonuç bildirdi ancak güvenlik bulguları okunamadı.");
        return result;
    }

    internal static PackageSecurityResult Parse(string output)
    {
        using var document = JsonDocument.Parse(output);
        var results = document.RootElement.GetProperty("results");
        if (results.ValueKind == JsonValueKind.Null)
            return new([], false);

        var findings = new List<PackageSecurityFinding>();
        var result = new PackageSecurityResult([], false);
        foreach (var source in results.EnumerateArray())
        foreach (var item in Items(source, "packages"))
        {
            var package = item.GetProperty("package");
            string name = Value(package, "name");
            string version = Value(package, "version");
            if (name.Length > 0)
                result.Packages.Add((name, version));
            var groups = Items(item, "groups").ToArray();
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var vulnerability in Items(item, "vulnerabilities"))
            {
                string id = Value(vulnerability, "id");
                if (name.Length == 0 || id.Length == 0)
                    continue;
                foreach (var affected in Items(vulnerability, "affected"))
                {
                    if (!affected.TryGetProperty("package", out var affectedPackage) ||
                        Value(affectedPackage, "name") != name || Value(affectedPackage, "ecosystem") != Value(package, "ecosystem"))
                        continue;
                    foreach (var range in Items(affected, "ranges").Where(range => Value(range, "type") is "SEMVER" or "ECOSYSTEM"))
                    foreach (var change in Items(range, "events"))
                    {
                        string fixedVersion = Value(change, "fixed");
                        if (fixedVersion.Length == 0)
                            continue;
                        if (!result.FixedVersions.TryGetValue(name, out var versions))
                            result.FixedVersions[name] = versions = new(StringComparer.Ordinal);
                        versions.Add(fixedVersion);
                    }
                }
                if (reported.Contains(id))
                    continue;
                var group = groups.FirstOrDefault(candidate => Items(candidate, "ids").Any(alias => alias.GetString() == id));
                foreach (var alias in Items(group, "ids"))
                    if (alias.GetString() is { } value)
                        reported.Add(value);
                reported.Add(id);
                findings.Add(new(name, version, Severity(group, vulnerability), id));
            }
        }
        return result with { Findings = findings.Distinct().ToArray(), HasPackages = result.Packages.Count > 0 };
    }

    private static string Severity(JsonElement group, JsonElement vulnerability)
    {
        if (double.TryParse(Value(group, "max_severity"), NumberStyles.Float, CultureInfo.InvariantCulture, out double score) && score > 0)
            return score switch { >= 9 => "Kritik", >= 7 => "Yüksek", >= 4 => "Orta", _ => "Düşük" };

        string label = vulnerability.TryGetProperty("database_specific", out var metadata) ? Value(metadata, "severity") : "";
        return label.ToUpperInvariant() switch
        {
            "CRITICAL" => "Kritik", "HIGH" => "Yüksek", "MODERATE" or "MEDIUM" => "Orta",
            "LOW" => "Düşük", _ => "Belirtilmedi"
        };
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];

    private static string Value(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
}
