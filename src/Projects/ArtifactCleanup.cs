using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace DevWorkspace;

internal sealed record ArtifactEntry(string RelativePath, long Bytes, long Written, FileAttributes Attributes, bool Directory);
internal sealed record ArtifactCandidate(string Folder, string ProjectRoot, string Kind, long Bytes, int Files, string Fingerprint,
    bool CanClean, string Detail, ArtifactEntry[] Entries)
{
    public string Label => $"{Folder} · {Bytes / 1048576d:N1} MB · {Files:N0} · {Detail}";
}
internal sealed record ArtifactReport(ArtifactCandidate[] Candidates, string[] Diagnostics);

internal static class ArtifactCleanup
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Cleaning = new(StringComparer.OrdinalIgnoreCase);
    internal static bool IsCleaning(string folder)
    {
        lock (Gate) return Cleaning.Any(root => folder.Equals(root, StringComparison.OrdinalIgnoreCase) || Within(folder, root) || Within(root, folder));
    }
    private sealed class Lease(string[] roots) : IDisposable
    { public void Dispose() { lock (Gate) foreach (string root in roots) Cleaning.Remove(root); } }
    private static IDisposable Acquire(string[] roots)
    {
        lock (Gate)
        {
            if (roots.Any(IsCleaning)) throw new IOException(WorkbenchText.T("CleanupRunning"));
            foreach (string root in roots) Cleaning.Add(root);
            return new Lease(roots);
        }
    }
    private static readonly string[] DotNet = [".csproj", ".fsproj", ".vbproj", ".vcxproj"];
    internal static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? Kind(string folder)
    {
        string name = Path.GetFileName(folder).ToLowerInvariant(), parent = Path.GetDirectoryName(folder)!;
        if (name is "bin" or "obj" && Directory.EnumerateFiles(parent).Any(f => DotNet.Contains(Path.GetExtension(f).ToLowerInvariant()))) return ".NET";
        if (name is "node_modules" or ".next" or ".nuxt" or "dist" && File.Exists(Path.Combine(parent, "package.json"))) return "JavaScript";
        if (name is "build" or ".dart_tool" && File.Exists(Path.Combine(parent, "pubspec.yaml"))) return "Dart";
        if (name == "target" && File.Exists(Path.Combine(parent, "Cargo.toml"))) return "Rust";
        if (name is "target" or "build" && (File.Exists(Path.Combine(parent, "pom.xml")) || File.Exists(Path.Combine(parent, "build.gradle")) || File.Exists(Path.Combine(parent, "build.gradle.kts")))) return "Java";
        if (name is "__pycache__" or ".pytest_cache") return "Python";
        return null;
    }

    private static void NoLinks(string path, string root)
    {
        string current = Path.GetFullPath(path), boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Within(current, boundary)) throw new IOException(WorkbenchText.T("CleanupOutside"));
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException(WorkbenchText.T("CleanupLink"));
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? throw new IOException(WorkbenchText.T("CleanupOutside"));
        }
    }

    internal static ArtifactCandidate Measure(string folder, string root, string kind, CancellationToken token)
    {
        NoLinks(folder, root);
        var entries = new List<ArtifactEntry>(); var pending = new Stack<string>(); pending.Push(folder);
        bool safe = true; string detail = WorkbenchText.T("CleanupReady");
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) { safe = false; detail = WorkbenchText.T("CleanupNestedRepository"); continue; }
                    if (entries.Count >= 100000) { safe = false; detail = WorkbenchText.T("CleanupLimit"); pending.Clear(); break; }
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { safe = false; detail = WorkbenchText.T("CleanupLink"); continue; }
                    bool child = (attributes & FileAttributes.Directory) != 0;
                    entries.Add(new(Path.GetRelativePath(folder, entry.FullName), child ? 0 : new FileInfo(entry.FullName).Length,
                        entry.LastWriteTimeUtc.Ticks, attributes, child));
                    if (child) pending.Push(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { safe = false; detail = ex.Message; }
        }
        var ordered = entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray();
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ordered.Select(e =>
            $"{e.RelativePath}|{e.Bytes}|{e.Written}|{(int)e.Attributes}|{e.Directory}")))));
        return new(Path.GetFullPath(folder), Path.GetFullPath(root), kind, entries.Sum(e => e.Bytes), entries.Count(e => !e.Directory),
            fingerprint, safe, detail, ordered);
    }

    internal static ArtifactReport Scan(ProjectEntry[] projects, CancellationToken token)
    {
        var candidates = new List<ArtifactCandidate>(); var diagnostics = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        foreach (string root in projects.Select(p => Path.GetFullPath(p.Folder)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length))
        {
            if (seen.Contains(root)) continue;
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(directory)) continue;
                if (++visited > 100000) { diagnostics.Add(WorkbenchText.T("CleanupLimit")); return new(candidates.ToArray(), diagnostics.ToArray()); }
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { diagnostics.Add(directory + ": " + WorkbenchText.T("CleanupLink")); continue; }
                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        if (Path.GetFileName(child).Equals(".git", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(child).StartsWith(".devworkspace-", StringComparison.OrdinalIgnoreCase)) continue;
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) { diagnostics.Add(child + ": " + WorkbenchText.T("CleanupLink")); continue; }
                        if (Kind(child) is { } kind)
                        { if (seen.Add(child)) candidates.Add(Measure(child, root, kind, token)); }
                        else pending.Push(child);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { diagnostics.Add(directory + ": " + ex.Message); }
            }
        }
        return new(candidates.OrderByDescending(c => c.Bytes).ToArray(), diagnostics.Distinct().Take(200).ToArray());
    }

    internal static async Task ValidateAsync(ArtifactCandidate candidate, string[] activeFolders, CancellationToken token)
    {
        if (!candidate.CanClean || Kind(candidate.Folder) != candidate.Kind || !Within(candidate.Folder, candidate.ProjectRoot))
            throw new IOException(WorkbenchText.T("CleanupChanged"));
        NoLinks(candidate.Folder, candidate.ProjectRoot);
        if (activeFolders.Any(folder => folder.Equals(candidate.ProjectRoot, StringComparison.OrdinalIgnoreCase) ||
            Within(folder, candidate.ProjectRoot) || Within(candidate.ProjectRoot, folder)))
            throw new IOException(WorkbenchText.T("CleanupRunning"));
        var current = Measure(candidate.Folder, candidate.ProjectRoot, candidate.Kind, token);
        if (!current.CanClean || current.Fingerprint != candidate.Fingerprint) throw new IOException(WorkbenchText.T("CleanupChanged"));
        var repo = await GitProjectOperations.RunAsync(candidate.ProjectRoot, ["rev-parse", "--show-toplevel"], token);
        if (repo.Code == 0)
        {
            string root = repo.Output.Trim(); string relative = Path.GetRelativePath(root, candidate.Folder).Replace('\\', '/');
            var tracked = await GitProjectOperations.RunAsync(root, ["ls-files", "-z", "--", ":(literal)" + relative + "/"], token);
            if (tracked.Code != 0) throw new IOException(tracked.Error);
            if (tracked.Output.Length != 0) throw new IOException(WorkbenchText.T("CleanupTracked"));
        }
        else
        {
            bool marker = false;
            for (string? directory = candidate.ProjectRoot; directory is not null; directory = Path.GetDirectoryName(directory))
                if (Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"))) { marker = true; break; }
            if (marker || repo.Code != 128 || !repo.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)) throw new IOException(repo.Error);
        }
    }

    internal static async Task<long> CleanAsync(ArtifactCandidate[] candidates, Func<string[]> activeFolders, bool permanent,
        CancellationToken token, Action<string, bool>? remove = null)
    {
        if (candidates.Length == 0) return 0;
        var selected = candidates.DistinctBy(c => c.Folder, StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Any(a => selected.Any(b => a != b && Within(a.Folder, b.Folder)))) throw new IOException(WorkbenchText.T("CleanupOutside"));
        using var lease = Acquire(selected.Select(c => c.ProjectRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        foreach (var candidate in selected) await ValidateAsync(candidate, activeFolders(), token);
        long moved = 0;
        foreach (var candidate in selected)
        {
            token.ThrowIfCancellationRequested();
            await ValidateAsync(candidate, activeFolders(), token);
            string staged = Path.Combine(Path.GetDirectoryName(candidate.Folder)!, ".devworkspace-cleanup-" + Guid.NewGuid().ToString("N"));
            if (!Within(staged, candidate.ProjectRoot)) throw new IOException(WorkbenchText.T("CleanupOutside"));
            Directory.Move(candidate.Folder, staged);
            try
            {
                var current = Measure(staged, candidate.ProjectRoot, candidate.Kind, token);
                if (!current.CanClean || current.Fingerprint != candidate.Fingerprint) throw new IOException(WorkbenchText.T("CleanupChanged"));
                NoLinks(staged, candidate.ProjectRoot);
                if (remove is not null) remove(staged, permanent);
                else if (permanent) Directory.Delete(staged, recursive: true);
                else FileSystem.DeleteDirectory(staged, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                if (Directory.Exists(staged)) throw new IOException(WorkbenchText.T("CleanupChanged"));
                moved += candidate.Bytes;
            }
            catch (Exception ex)
            {
                if (Directory.Exists(staged) && !Directory.Exists(candidate.Folder)) Directory.Move(staged, candidate.Folder);
                throw new IOException(ex.Message + Environment.NewLine + WorkbenchText.T("CleanupRecovery") + " " +
                    (Directory.Exists(staged) ? staged : candidate.Folder), ex);
            }
        }
        return moved;
    }
}
