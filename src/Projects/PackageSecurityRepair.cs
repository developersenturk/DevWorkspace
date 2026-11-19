using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal static partial class PackageSecurityRepair
{
    internal static DevelopmentTool? RequiredTool(string lockfile) => Path.GetFileName(lockfile) switch
    {
        "package-lock.json" or "pnpm-lock.yaml" => DevelopmentTool.Node,
        "pubspec.lock" => DevelopmentTool.Flutter,
        _ => null
    };

    internal static async Task<(PackageSecurityResult Result, string Detail)> RepairAsync(
        string lockfile, PackageSecurityResult before, Action verifyProgress, CancellationToken token)
    {
        string folder = Path.GetDirectoryName(lockfile)!;
        string name = Path.GetFileName(lockfile);
        string manifest = Path.Combine(folder, name == "pubspec.lock" ? "pubspec.yaml" : "package.json");
        if (!File.Exists(manifest))
            return (before, Loc.Format("LE70DC29DB67C", Path.GetFileName(manifest)));
        if ((File.GetAttributes(lockfile) & FileAttributes.ReparsePoint) != 0)
            return (before, Loc.Text("L1A55645D3B6B"));

        var names = before.Findings.Select(finding => finding.Name).Distinct(StringComparer.Ordinal).ToArray();
        ProcessStartInfo info;
        switch (name)
        {
            case "package-lock.json":
                if (File.Exists(Path.Combine(folder, "npm-shrinkwrap.json")))
                    return (before, Loc.Text("LE50D1E196B33"));
                info = CreateNpmInfo(folder,
                    ["audit", "fix", "--package-lock-only", "--package-lock=true", "--ignore-scripts", "--force=false", "--no-fund", "--json"]);
                break;
            case "pnpm-lock.yaml":
                string[] npmNames = names.Where(value => NpmName().IsMatch(value)).ToArray();
                if (npmNames.Length == 0)
                    return (before, Loc.Text("LD814CA4A57FF"));
                string[] scope = File.Exists(Path.Combine(folder, "pnpm-workspace.yaml"))
                    ? ["--recursive", "--config.include-workspace-root=true"] : [];
                info = CreatePnpmInfo(folder,
                    [.. scope, "update", "--lockfile-only", "--no-save", "--ignore-scripts", "--depth", "Infinity", .. npmNames]);
                break;
            case "pubspec.lock":
                string[] pubNames = names.Where(value => PubName().IsMatch(value)).ToArray();
                if (pubNames.Length == 0)
                    return (before, Loc.Text("L1297B3AD2725"));
                info = CreateFlutterInfo(folder,
                    ["pub", "upgrade", "--no-example", "--no-precompile", .. pubNames]);
                break;
            default:
                return (before, Loc.Text("L3626C885FBC3"));
        }

        byte[] original = await File.ReadAllBytesAsync(lockfile, token);
        bool keepUpdate = false;
        try
        {
            var (code, output, error) = await ProjectTools.CaptureAsync(info, Loc.Text("LB9C9D2461F3B"), 300, token);
            // npm audit fix can exit with 1 when some advisories remain after a partial fix.
            if (code != 0 && !(name == "package-lock.json" && code == 1 && HasNpmAuditReport(output)))
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                throw new IOException(Loc.Format("L814311292880", code, detail[..Math.Min(detail.Length, 350)]));
            }

            verifyProgress();
            var after = await PackageSecurity.ScanAsync(lockfile, token);
            if (!after.HasPackages)
                throw new IOException(Loc.Text("L389A1F23EFFA"));

            var known = before.Findings.Select(finding => (finding.Name, finding.Advisory)).ToHashSet();
            if (after.Findings.Any(finding => !known.Contains((finding.Name, finding.Advisory))))
                return (before, Loc.Text("L709F3A902151"));
            if (after.Findings.Length >= before.Findings.Length)
                return (before, Loc.Text("LCD9AD53891DA"));

            token.ThrowIfCancellationRequested();
            keepUpdate = true;
            return (after, Loc.Format("L61E071ABC66F", before.Findings.Length, after.Findings.Length));
        }
        finally
        {
            if (!keepUpdate)
                RestoreFile(lockfile, original);
        }
    }

    private static void RestoreFile(string path, byte[] original)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
            return;
        ReplaceFile(path, original);
    }

    private static void ReplaceFile(string path, byte[] content)
    {
        // Write the replacement completely before renaming it over the project file.
        string replacement = Path.Combine(Path.GetDirectoryName(path)!, $".devworkspace-{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(replacement, content);
        try { File.Move(replacement, path, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(Loc.Format("LA25A83417818", replacement), ex);
        }
    }

    private static ProcessStartInfo CreateNpmInfo(string folder, string[] arguments)
    {
        string node = DevelopmentTools.Require(DevelopmentTool.Node);
        string npm = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npm))
            throw new ToolMissingException(DevelopmentTools.Get(DevelopmentTool.Node));
        return ProjectTools.CreateCaptureInfo(node, folder, [npm, "--global=false", "--prefix=.", .. arguments]);
    }

    private static ProcessStartInfo CreatePnpmInfo(string folder, string[] arguments) =>
        CreateBatchInfo(DevelopmentTools.FindPnpm() ??
            throw new IOException(Loc.Text("L468E99283600")),
            folder, ["--global=false", "--dir=.", .. arguments]);

    private static ProcessStartInfo CreateFlutterInfo(string folder, string[] arguments) =>
        CreateBatchInfo(DevelopmentTools.Require(DevelopmentTool.Flutter), folder, arguments);

    private static bool HasNpmAuditReport(string output)
    {
        try
        {
            using var report = JsonDocument.Parse(output);
            return report.RootElement.ValueKind == JsonValueKind.Object &&
                !report.RootElement.TryGetProperty("error", out _) &&
                report.RootElement.TryGetProperty("audit", out var audit) && audit.ValueKind == JsonValueKind.Object &&
                !audit.TryGetProperty("error", out _) && audit.TryGetProperty("metadata", out _);
        }
        catch (JsonException) { return false; }
    }

    private static ProcessStartInfo CreateBatchInfo(string executable, string folder, string[] arguments)
    {
        if (Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return ProjectTools.CreateCaptureInfo(executable, folder, arguments);

        // Arguments are fixed switches or validated package names. The absolute shim path is expanded once.
        var info = ProjectTools.CreateCaptureInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), folder, []);
        info.Arguments = $"/d /v:off /s /c \"\"%DEVWORKSPACE_PACKAGE_TOOL%\" {string.Join(' ', arguments)}\"";
        info.Environment["DEVWORKSPACE_PACKAGE_TOOL"] = executable;
        DevelopmentTools.ApplyPath(info, Path.GetDirectoryName(executable)!);
        return info;
    }

    [GeneratedRegex(@"\A(?:@[a-z0-9_.][a-z0-9._-]*/)?[a-z0-9_][a-z0-9._-]*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmName();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PubName();
}
