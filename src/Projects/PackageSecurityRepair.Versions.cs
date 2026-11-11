using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed record PackageVersionChange(string Name, Version Current, Version Target, int Start, int Length,
    string? CurrentConstraint = null, string[]? FixedDependencies = null);
internal sealed record PackageVersionPlan(string Manifest, string Text, bool HasBom,
    Dictionary<string, byte[]> Originals, Dictionary<string, PackageSecurityResult> Scans, PackageVersionChange[] Changes)
{
    internal HashSet<(string Name, string Advisory)> ExpectedFixes { get; init; } = [];
}

internal static partial class PackageSecurityRepair
{
    private static readonly HttpClient AdvisoryClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly UTF8Encoding ManifestEncoding = new(false, true);

    internal static async Task<PackageVersionPlan?> PlanVersionsAsync(string lockfile, CancellationToken token,
        Action<string>? trialProgress = null)
    {
        string folder = Path.GetDirectoryName(lockfile)!;
        bool flutter = Path.GetFileName(lockfile) == "pubspec.lock";
        string manifest = Path.Combine(folder, flutter ? "pubspec.yaml" : "package.json");
        if (!File.Exists(manifest))
            return null;
        string[] locks = flutter ? [lockfile] : ExistingJavaScriptLocks(folder);
        if (!flutter)
            CheckJavaScriptLocks(folder, locks);
        var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in locks.Prepend(manifest))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Bağlantı olarak tanımlanan dosya elle güncellenmeli. {Path.GetFileName(path)}");
            originals.Add(path, await File.ReadAllBytesAsync(path, token));
        }
        var manifestBytes = originals[manifest];
        bool hasBom = manifestBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        int bomLength = hasBom ? Encoding.UTF8.Preamble.Length : 0;
        string text = ManifestEncoding.GetString(manifestBytes, bomLength, manifestBytes.Length - bomLength);
        var pins = flutter ? ReadPubPins(text) : ReadNpmPins(text);
        if (pins.Length == 0)
            return null;

        var scans = new Dictionary<string, PackageSecurityResult>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in locks)
            scans.Add(path, await PackageSecurity.ScanAsync(path, token));
        if (scans.Values.Any(scan => !scan.HasPackages))
            throw new IOException("Onaylı güncelleme için ilgili kilit dosyalarının tamamı taranabilmeli.");

        var plan = new PackageVersionPlan(manifest, text, hasBom, originals, scans, []);
        var transitive = await PlanTransitiveVersionsAsync(plan, pins, trialProgress, token);
        if (transitive is not null)
            return transitive;

        var changes = new List<PackageVersionChange>();
        foreach (var group in pins.Where(pin => pin.Prefix.Length == 0).GroupBy(pin => pin.Name, StringComparer.Ordinal))
        {
            var pin = group.First();
            if (group.Any(other => other.Current != pin.Current) ||
                !scans.Values.Any(scan => scan.Findings.Any(finding => finding.Name == pin.Name && finding.Version == pin.Current.ToString())))
                continue;

            var firstPatch = scans.Values.Where(scan => scan.FixedVersions.ContainsKey(pin.Name))
                .SelectMany(scan => scan.FixedVersions[pin.Name]).Select(StableVersion).OfType<Version>()
                .Where(version => version > pin.Current).Min();
            var target = await FindPublishedFixAsync(flutter, pin, firstPatch, token);
            if (target is not null)
                changes.AddRange(group.Select(item => new PackageVersionChange(item.Name, item.Current, target, item.Start, item.Length)));
        }

        return changes.Count == 0 ? null : plan with { Changes = changes.ToArray() };
    }

    internal static async Task<int> ApplyVersionsAsync(PackageVersionPlan plan, Action verifyProgress, CancellationToken token) =>
        (await ResolveVersionsAsync(plan, verifyProgress, preview: false, token)).Resolved;

    private static async Task<(int Resolved, HashSet<(string Name, string Advisory)> Cleared)> ResolveVersionsAsync(
        PackageVersionPlan plan, Action verifyProgress, bool preview, CancellationToken token)
    {
        string folder = Path.GetDirectoryName(plan.Manifest)!;
        var commands = plan.Scans.Keys.Select(path => Path.GetFileName(path) switch
        {
            "package-lock.json" => CreateNpmInfo(folder,
                ["install", "--package-lock-only", "--package-lock=true", "--ignore-scripts", "--force=false", "--no-audit", "--no-fund"]),
            "pnpm-lock.yaml" => CreatePnpmInfo(folder,
                ["install", "--lockfile-only", "--lockfile-dir=.", "--no-frozen-lockfile", "--ignore-scripts",
                    .. preview ? new[] { "--ignore-pnpmfile" } : Array.Empty<string>()]),
            _ => CreateFlutterInfo(folder, ["pub", "get", "--no-example", "--no-precompile"])
        }).ToArray();
        foreach (var (path, original) in plan.Originals)
        {
            var bytes = await File.ReadAllBytesAsync(path, token);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !bytes.AsSpan().SequenceEqual(original))
                throw new IOException($"Dosya inceleme sırasında değişti. Yeniden tarayın. {Path.GetFileName(path)}");
        }
        if (Path.GetFileName(plan.Manifest) == "package.json")
        {
            string[] locks = ExistingJavaScriptLocks(folder);
            CheckJavaScriptLocks(folder, locks);
            if (!plan.Scans.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(locks))
                throw new IOException("İnceleme sırasında kilit dosyaları değişti. Yeniden tarayın.");
        }

        var text = new StringBuilder(plan.Text);
        foreach (var change in plan.Changes.OrderByDescending(change => change.Start))
            text.Remove(change.Start, change.Length).Insert(change.Start, change.Target.ToString());
        byte[] updated = ManifestEncoding.GetBytes((plan.HasBom ? "\uFEFF" : "") + text);

        bool keep = false;
        try
        {
            token.ThrowIfCancellationRequested();
            ReplaceFile(plan.Manifest, updated);
            foreach (var command in commands)
            {
                var (code, output, error) = await ProjectTools.CaptureAsync(command, "Paket sürümü çözümlemesi", preview ? 45 : 300, token);
                if (code != 0)
                {
                    string detail = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
                    throw new IOException($"Paket yöneticisi onaylanan sürümü çözümleyemedi (kod {code}). {detail[..Math.Min(detail.Length, 350)]}");
                }
            }

            verifyProgress();
            if (!(await File.ReadAllBytesAsync(plan.Manifest, token)).AsSpan().SequenceEqual(updated))
                throw new IOException("Paket yöneticisi onaylanan sürüm değişikliği dışında paket bildirimini düzenledi.");
            int resolved = 0;
            var remaining = new HashSet<(string Name, string Advisory)>();
            foreach (var (path, before) in plan.Scans)
            {
                var after = await PackageSecurity.ScanAsync(path, token);
                if (!after.HasPackages || plan.Changes.Any(change => before.Packages.Any(package => package.Name == change.Name) &&
                        !after.Packages.Contains((change.Name, change.Target.ToString()))))
                    throw new IOException($"Onaylanan paket sürümü kilit dosyasında doğrulanamadı. {Path.GetFileName(path)}");
                var known = before.Findings.Select(finding => (finding.Name, finding.Advisory)).ToHashSet();
                if (after.Findings.Any(finding => !known.Contains((finding.Name, finding.Advisory))))
                    throw new IOException($"Güncelleme yeni bir açık getirdi. {Path.GetFileName(path)}");
                remaining.UnionWith(after.Findings.Select(finding => (finding.Name, finding.Advisory)));
                resolved += before.Findings.Length - after.Findings.Length;
            }
            if (resolved <= 0)
                throw new IOException("Güncelleme doğrulanan açık sayısını azaltmadı.");
            if (plan.ExpectedFixes.Overlaps(remaining))
                throw new IOException("Önizlemede giderileceği gösterilen dolaylı açıklar yeniden taramada doğrulanamadı.");
            token.ThrowIfCancellationRequested();
            keep = true;
            var cleared = plan.Scans.Values.SelectMany(scan => scan.Findings).Select(finding => (finding.Name, finding.Advisory))
                .Where(finding => !remaining.Contains(finding)).ToHashSet();
            return (resolved, cleared);
        }
        finally
        {
            if (!keep)
            {
                var failures = new List<string>();
                foreach (var (path, original) in plan.Originals)
                {
                    try { RestoreFile(path, original); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(ex.Message); }
                }
                if (failures.Count > 0)
                    throw new IOException(string.Join(" · ", failures));
            }
        }
    }

    private static string[] ExistingJavaScriptLocks(string folder) =>
        new[] { "package-lock.json", "pnpm-lock.yaml" }.Select(name => Path.Combine(folder, name)).Where(File.Exists).ToArray();

    private static void CheckJavaScriptLocks(string folder, string[] locks)
    {
        if (locks.Length == 0 || new[] { "npm-shrinkwrap.json", "yarn.lock", "bun.lock", "bun.lockb" }
            .Any(name => File.Exists(Path.Combine(folder, name))))
            throw new IOException("Bu klasördeki kilit dosyalarının tamamı onaylı sürüm güncellemesini desteklemiyor.");
        if (locks.Any(path => Path.GetFileName(path) == "pnpm-lock.yaml") && !File.Exists(Path.Combine(folder, "pnpm-workspace.yaml")))
        {
            for (string? parent = Path.GetDirectoryName(folder); parent is not null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(Path.Combine(parent, "pnpm-workspace.yaml")))
                    throw new IOException("Sürüm değişikliğini üst pnpm çalışma alanında inceleyin.");
        }
    }

    private static async Task<Version?> FindPublishedFixAsync(bool flutter, PackagePin pin, Version? firstPatch,
        CancellationToken token)
    {
        var published = await ReadPublishedVersionsAsync(flutter, pin.Name, token);
        var candidates = published.Where(version => version > pin.Current && (firstPatch is null || version >= firstPatch)).Order();
        foreach (var batch in candidates.Chunk(100))
        {
            var clean = await FirstCleanVersionAsync(flutter ? "Pub" : "npm", pin.Name, batch, token);
            if (clean is not null)
                return clean;
        }
        return null;
    }

    private static async Task<Version[]> ReadPublishedVersionsAsync(bool flutter, string package, CancellationToken token)
    {
        string name = Uri.EscapeDataString(package);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            flutter ? $"https://pub.dev/api/packages/{name}" : $"https://registry.npmjs.org/{name}");
        request.Headers.Accept.ParseAdd(flutter ? "application/vnd.pub.v2+json" : "application/vnd.npm.install-v1+json");
        using var response = await AdvisoryClient.SendAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var entries = metadata.RootElement.GetProperty("versions");
        IEnumerable<string?> published = flutter
            ? entries.EnumerateArray().Where(item => !item.TryGetProperty("retracted", out var value) || value.ValueKind != JsonValueKind.True)
                .Select(item => item.GetProperty("version").GetString())
            : entries.EnumerateObject().Where(item => !item.Value.TryGetProperty("deprecated", out _)).Select(item => item.Name);
        return published.Select(StableVersion).OfType<Version>().Distinct().Order().ToArray();
    }

    private static async Task<Version?> FirstCleanVersionAsync(string ecosystem, string name, Version[] candidates, CancellationToken token)
    {
        var queries = candidates.Select(version => new { package = new { ecosystem, name }, version = version.ToString() }).ToArray();
        using var response = await AdvisoryClient.PostAsJsonAsync("https://api.osv.dev/v1/querybatch", new { queries }, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var results = json.RootElement.GetProperty("results");
        if (results.GetArrayLength() != candidates.Length)
            throw new IOException("OSV sürüm sorgusu eksik sonuç döndürdü.");
        for (int index = 0; index < candidates.Length; index++)
        {
            var result = results[index];
            if (result.ValueKind == JsonValueKind.Object && !result.TryGetProperty("next_page_token", out _) &&
                (!result.TryGetProperty("vulns", out var vulnerabilities) ||
                 vulnerabilities.ValueKind == JsonValueKind.Array && vulnerabilities.GetArrayLength() == 0))
                return candidates[index];
        }
        return null;
    }

    private sealed record PackagePin(string Name, Version Current, int Start, int Length, string Prefix);

    private static PackagePin? ReadPin(string name, string value, int start, int length)
    {
        string prefix = value.StartsWith('^') || value.StartsWith('~') ? value[..1] : "";
        return StableVersion(value[prefix.Length..]) is { } version ? new(name, version, start, length, prefix) : null;
    }

    private static PackagePin[] ReadNpmPins(string text)
    {
        var pins = new List<PackagePin>();
        byte[] bytes = ManifestEncoding.GetBytes(text);
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                reader.GetString() is not ("dependencies" or "devDependencies" or "optionalDependencies"))
                continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                continue;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string name = reader.GetString()!;
                if (!reader.Read())
                    break;
                if (reader.TokenType == JsonTokenType.String && NpmName().IsMatch(name))
                {
                    int start = ManifestEncoding.GetCharCount(bytes.AsSpan(0, (int)reader.TokenStartIndex)) + 1;
                    int length = ManifestEncoding.GetCharCount(reader.ValueSpan);
                    if (ReadPin(name, reader.GetString()!, start, length) is { } pin)
                        pins.Add(pin);
                }
                reader.Skip();
            }
        }
        return pins.ToArray();
    }

    private static PackagePin[] ReadPubPins(string text)
    {
        var pins = new List<PackagePin>();
        // Only edit plain or quoted version scalars in a normal top-level dependency mapping.
        foreach (Match section in PubDependencies().Matches(text))
        {
            var body = section.Groups["body"];
            var entries = PubEntry().Matches(body.Value);
            var indents = PubIndent().Matches(body.Value);
            if (indents.Count == 0)
                continue;
            int indent = indents.Min(entry => entry.Length);
            foreach (Match entry in entries)
            {
                var value = entry.Groups["version"];
                if (entry.Groups["indent"].Length == indent && value.Success &&
                    ReadPin(entry.Groups["name"].Value, value.Value, body.Index + value.Index, value.Length) is { } pin)
                    pins.Add(pin);
            }
        }
        return pins.ToArray();
    }

    private static Version? StableVersion(string? value) =>
        Version.TryParse(value, out var version) && version.Build >= 0 && version.Revision < 0 && version.ToString() == value ? version : null;

    [GeneratedRegex(@"^(?:dependencies|dev_dependencies):[ \t]*(?:#[^\r\n]*)?\r?\n(?<body>(?:(?: +[^\r\n]*|[ \t]*)\r?(?:\n|\z))*)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PubDependencies();

    [GeneratedRegex(@"^ +(?![ \t#\r\n])(?=[^\r\n])", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PubIndent();

    [GeneratedRegex("""^(?<indent> +)(?<name>[a-z][a-z0-9_]*):[ \t]*(?:(?<quote>['"]?)(?<version>\^?[0-9]+\.[0-9]+\.[0-9]+)\k<quote>[ \t]*(?:#[^\r\n]*)?\r?$)?""",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PubEntry();
}
