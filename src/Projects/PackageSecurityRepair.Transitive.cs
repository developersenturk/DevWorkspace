using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal static partial class PackageSecurityRepair
{
    private sealed record DependencyParent(string Name, Version Version, HashSet<(string Name, string Version)> Descendants);
    private sealed record UpgradeTrial(PackagePin[] Pins, Version Current, Version[] Targets,
        HashSet<(string Name, string Advisory)> Findings);
    private static readonly string[] DependencyGroups = ["dependencies", "devDependencies", "optionalDependencies"];

    private static async Task<PackageVersionPlan?> PlanTransitiveVersionsAsync(PackageVersionPlan plan, PackagePin[] pins,
        Action<string>? progress, CancellationToken token)
    {
        var findings = plan.Scans.Values.SelectMany(scan => scan.Findings).ToArray();
        if (findings.All(finding => pins.Any(pin => pin.Name == finding.Name && pin.Prefix.Length == 0 &&
                pin.Current.ToString() == finding.Version)) || !CanPreviewDependencies(plan))
            return null;

        string folder = Path.GetDirectoryName(plan.Manifest)!;
        bool flutter = Path.GetFileName(plan.Manifest) == "pubspec.yaml";
        string scratch = Directory.CreateTempSubdirectory("DevWorkspace-dependencies-").FullName;
        try
        {
            var copies = plan.Originals.ToDictionary(item => Path.Combine(scratch, Path.GetFileName(item.Key)), item => item.Value);
            foreach (var (path, bytes) in copies)
                await File.WriteAllBytesAsync(path, bytes, token);
            foreach (string config in new[] { ".npmrc", "pnpm-workspace.yaml" })
            {
                string path = Path.Combine(folder, config);
                if (!flutter && File.Exists(path))
                    File.Copy(path, Path.Combine(scratch, config));
            }
            var scans = plan.Scans.ToDictionary(item => Path.Combine(scratch, Path.GetFileName(item.Key)), item => item.Value);
            var staged = plan with { Manifest = Path.Combine(scratch, Path.GetFileName(plan.Manifest)), Originals = copies, Scans = scans };
            var graphs = new List<(DependencyParent Parent, PackageSecurityFinding[] Findings)>();
            foreach (var (lockfile, report) in scans)
            {
                if (report.Findings.Length == 0)
                    continue;
                var parents = await ReadDependencyParentsAsync(lockfile, token);
                graphs.AddRange(parents.Select(parent => (parent, report.Findings)));
            }
            if (copies.Any(file => !File.ReadAllBytes(file.Key).AsSpan().SequenceEqual(file.Value)))
                return null;

            var trials = new List<UpgradeTrial>();
            foreach (var group in pins.GroupBy(pin => pin.Name, StringComparer.Ordinal))
            {
                var sameName = graphs.Where(item => item.Parent.Name == group.Key).ToArray();
                var versions = sameName.Select(item => item.Parent.Version).Distinct().ToArray();
                if (versions.Length != 1 || group.Select(pin => (pin.Prefix, pin.Current)).Distinct().Count() != 1)
                    continue;
                var affected = sameName.SelectMany(item => item.Findings
                    .Where(finding => item.Parent.Descendants.Contains((finding.Name, finding.Version))))
                    .Select(finding => (finding.Name, finding.Advisory)).ToHashSet();
                if (affected.Count == 0)
                    continue;

                var published = await ReadPublishedVersionsAsync(flutter, group.Key, token);
                var candidates = PreviewCandidates(published, versions[0]);
                if (candidates.Length > 0)
                    trials.Add(new(group.ToArray(), versions[0], candidates, affected));
            }
            if (trials.Count == 0)
                return null;

            var accepted = new List<PackageVersionChange>();
            var expected = new HashSet<(string Name, string Advisory)>();
            int remainingAttempts = 16;
            // Parents that share an advisory must be tested together, since either branch can keep it in the lockfile.
            while (trials.Count > 0 && remainingAttempts > 0)
            {
                var group = new List<UpgradeTrial> { trials[0] };
                var related = new HashSet<(string Name, string Advisory)>(trials[0].Findings);
                trials.RemoveAt(0);
                for (int index = 0; index < trials.Count;)
                {
                    var next = trials[index];
                    if (!related.Overlaps(next.Findings))
                    {
                        index++;
                        continue;
                    }
                    related.UnionWith(next.Findings);
                    group.Add(next);
                    trials.RemoveAt(index);
                    index = 0;
                }
                PackageVersionChange[] best = [];
                var bestFixes = new HashSet<(string Name, string Advisory)>();
                for (int round = 0; round < group.Max(item => item.Targets.Length) && remainingAttempts > 0; round++)
                {
                    remainingAttempts--;
                    var proposed = group.SelectMany(item => item.Pins.Select(pin => new PackageVersionChange(
                        pin.Name, item.Current, item.Targets[Math.Min(round, item.Targets.Length - 1)], pin.Start, pin.Length,
                        pin.Prefix + pin.Current))).ToArray();
                    progress?.Invoke(Loc.Format("L01644CB9C27E", string.Join(", ", proposed.DistinctBy(change => change.Name).Select(change => change.Name))));
                    foreach (var (path, bytes) in copies)
                        RestoreFile(path, bytes);
                    HashSet<(string Name, string Advisory)> cleared;
                    try
                    {
                        (_, cleared) = await ResolveVersionsAsync(staged with { Changes = [.. accepted, .. proposed], ExpectedFixes = expected },
                            () => { }, preview: true, token);
                    }
                    catch (IOException) { continue; }
                    if (group.Any(item => !item.Findings.Overlaps(cleared)))
                        continue;
                    var fixedFindings = related.Where(cleared.Contains).ToHashSet();
                    if (fixedFindings.Count <= bestFixes.Count)
                        continue;
                    best = proposed;
                    bestFixes = fixedFindings;
                    if (related.IsSubsetOf(cleared))
                        break;
                }
                foreach (var change in best)
                {
                    var fixedPackages = group.First(item => item.Pins[0].Name == change.Name).Findings
                        .Where(bestFixes.Contains).ToArray();
                    accepted.Add(change with { FixedDependencies = fixedPackages.Select(item => item.Name).Distinct().Order().ToArray() });
                }
                expected.UnionWith(bestFixes);
            }
            return accepted.Count == 0 ? null : plan with { Changes = accepted.ToArray(), ExpectedFixes = expected };
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or JsonException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Version[] PreviewCandidates(Version[] published, Version current)
    {
        var newer = published.Where(version => version > current).ToArray();
        return newer.Where(version => version.Major == current.Major && version.Minor == current.Minor).TakeLast(1)
            .Concat(newer.Where(version => version.Major == current.Major).TakeLast(1))
            .Concat(newer.Where(version => version.Major > current.Major).GroupBy(version => version.Major).Take(3)
                .Select(line => line.Last()))
            .Concat(newer.TakeLast(1)).Distinct().ToArray();
    }

    private static bool CanPreviewDependencies(PackageVersionPlan plan)
    {
        string folder = Path.GetDirectoryName(plan.Manifest)!;
        if (Path.GetFileName(plan.Manifest) == "pubspec.yaml")
            return !Regex.IsMatch(plan.Text, @"(?m)^(?:workspace|resolution)\s*:") &&
                !File.Exists(Path.Combine(folder, "pubspec_overrides.yaml")) &&
                plan.Scans.Keys.All(path => !Regex.IsMatch(ManifestEncoding.GetString(plan.Originals[path]), @"(?m)^\s*source: (?:path|git)\s*$"));

        using var json = JsonDocument.Parse(plan.Text);
        if (json.RootElement.TryGetProperty("workspaces", out _))
            return false;
        string workspace = Path.Combine(folder, "pnpm-workspace.yaml");
        if (File.Exists(workspace) && Regex.IsMatch(File.ReadAllText(workspace), @"(?m)^\s*(?:packages|patchedDependencies)\s*:"))
            return false;
        return plan.Scans.Keys.All(path =>
        {
            string content = ManifestEncoding.GetString(plan.Originals[path]);
            return !Regex.IsMatch(content, """(?:["'\s])(?:link|file|workspace):|"link"\s*:\s*true""");
        });
    }

    private static async Task<DependencyParent[]> ReadDependencyParentsAsync(string lockfile, CancellationToken token)
    {
        string folder = Path.GetDirectoryName(lockfile)!;
        string file = Path.GetFileName(lockfile);
        var info = file switch
        {
            "package-lock.json" => CreateNpmInfo(folder, ["ls", "--package-lock-only", "--all", "--json"]),
            "pnpm-lock.yaml" => CreatePnpmInfo(folder, ["list", "--lockfile-only", "--depth", "Infinity", "--json"]),
            _ => CreateFlutterInfo(folder, ["pub", "deps", "--json"])
        };
        var (code, output, _) = await ProjectTools.CaptureAsync(info, Loc.Text("L40A31C4111B3"), 35, token);
        if (code != 0)
            return [];
        using var json = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 256 });
        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() != 1)
                return [];
            root = root[0];
        }
        return file == "pubspec.lock" ? PubParents(root) :
            JavaScriptChildren(root).Select(child =>
            {
                var descendants = new HashSet<(string Name, string Version)>();
                AddJavaScriptDescendants(child.Value, descendants);
                return StableVersion(child.Value.GetProperty("version").GetString()) is { } version
                    ? new DependencyParent(child.Name, version, descendants) : null;
            }).OfType<DependencyParent>().ToArray();
    }

    private static IEnumerable<JsonProperty> JavaScriptChildren(JsonElement node) =>
        DependencyGroups.Where(group => node.ValueKind == JsonValueKind.Object &&
                node.TryGetProperty(group, out var children) && children.ValueKind == JsonValueKind.Object)
            .SelectMany(group => node.GetProperty(group).EnumerateObject())
            .Where(child => child.Value.ValueKind == JsonValueKind.Object && child.Value.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String);

    private static void AddJavaScriptDescendants(JsonElement parent, HashSet<(string Name, string Version)> descendants)
    {
        foreach (var child in JavaScriptChildren(parent))
        {
            string name = child.Value.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String
                ? from.GetString()! : child.Name;
            descendants.Add((name, child.Value.GetProperty("version").GetString()!));
            AddJavaScriptDescendants(child.Value, descendants);
        }
    }

    private static DependencyParent[] PubParents(JsonElement root)
    {
        var packages = root.GetProperty("packages").EnumerateArray()
            .ToDictionary(package => package.GetProperty("name").GetString()!);
        string name = root.GetProperty("root").GetString()!;
        return PubChildren(packages[name]).Where(packages.ContainsKey).Select(parent =>
        {
            var descendants = new HashSet<(string Name, string Version)>();
            var pending = new Stack<string>(PubChildren(packages[parent]));
            while (pending.TryPop(out string? child))
            {
                if (!packages.TryGetValue(child, out var package) ||
                    !descendants.Add((child, package.GetProperty("version").GetString()!)))
                    continue;
                foreach (string dependency in PubChildren(package))
                    pending.Push(dependency);
            }
            return StableVersion(packages[parent].GetProperty("version").GetString()) is { } version
                ? new DependencyParent(parent, version, descendants) : null;
        }).OfType<DependencyParent>().ToArray();
    }

    private static IEnumerable<string> PubChildren(JsonElement parent) =>
        parent.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array
            ? dependencies.EnumerateArray().Select(child => child.GetString()!) : [];
}
