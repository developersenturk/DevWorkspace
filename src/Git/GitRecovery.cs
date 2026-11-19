using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed record GitRecoveryEntry(string Folder, string Remote, string Url, string? Branch)
{
    [JsonIgnore] public string BranchLabel => Branch ?? Loc.Text("L9AEE1D2C46B5");
}

internal sealed class GitRecovery(string filePath)
{
    internal const string StagingPrefix = ".devworkspace-restore-";
    internal const string FileName = "git-recovery.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static readonly string LegacyFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevWorkspace");

    internal static GitRecovery InFolder(string folder) => new(Path.Combine(folder, FileName));

    public void AdoptFrom(GitRecovery previous, CancellationToken token) =>
        Save(previous.Load(), overwrite: false, token);

    public async Task RememberAsync(IEnumerable<ProjectEntry> projects, CancellationToken token)
    {
        CheckBackupFolder();
        var entries = new ConcurrentBag<GitRecoveryEntry>();
        await Parallel.ForEachAsync(projects.Where(project => project.Repository is not null &&
                project.Git.Remotes is { Count: > 0 }).DistinctBy(project => project.Repository, StringComparer.OrdinalIgnoreCase),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (project, ct) =>
            {
                try
                {
                    string folder = project.Repository!;
                    var git = new GitWorkbench(folder);
                    var upstream = await git.ReadUpstreamAsync(project.Git.HeadRef, ct);
                    var remotes = project.Git.Remotes!;
                    bool tracked = upstream is not null && remotes.Contains(upstream.Remote, StringComparer.Ordinal);
                    string? remote = tracked ? upstream!.Remote : remotes.Contains("origin", StringComparer.Ordinal) ? "origin" :
                        remotes.Count == 1 ? remotes[0] : null;
                    if (remote is null)
                        return;

                    string? url = (await git.ReadRemoteAsync(remote, ct)).Url;
                    if (url is null)
                        return;
                    if (!IsNetworkUrl(url) && !IsScpUrl(url))
                    {
                        if (!Path.IsPathFullyQualified(url) && url.Contains(':'))
                            return;
                        url = Path.GetFullPath(url, folder);
                    }

                    string? branch = tracked ? upstream!.Ref["refs/heads/".Length..] : null;
                    entries.Add(new(folder, remote, url, branch));
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or ArgumentException or NotSupportedException) { }
            });

        if (entries.IsEmpty)
            return;

        Save(entries, overwrite: true, token);
    }

    private void Save(IEnumerable<GitRecoveryEntry> entries, bool overwrite, CancellationToken token)
    {
        using var mutex = new Mutex(false, @"Local\DevWorkspace.GitRecovery");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException(Loc.Text("L091A1C1F0F44"));
        }
        catch (AbandonedMutexException) { }
        try
        {
            token.ThrowIfCancellationRequested();
            CheckBackupFolder();
            var saved = Load().ToDictionary(entry => entry.Folder, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (var entry in entries)
            {
                if (!saved.TryGetValue(entry.Folder, out var previous) || (overwrite && previous != entry))
                {
                    saved[entry.Folder] = entry;
                    changed = true;
                }
            }
            if (!changed && File.Exists(filePath))
                return;

            string temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var ordered = saved.Values.OrderBy(entry => entry.Folder, StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(temporary, JsonSerializer.Serialize(ordered, JsonOptions));
                File.Move(temporary, filePath, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public GitRecoveryEntry[] Missing()
    {
        CheckBackupFolder();
        return Load().Where(entry => !Exists(entry.Folder))
            .OrderBy(entry => entry.Folder.Length).ThenBy(entry => entry.Folder, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void CheckBackupFolder()
    {
        if (!Directory.Exists(Path.GetDirectoryName(filePath)))
            throw new IOException(Loc.Text("LB904D17F0E25"));
    }

    public static async Task RestoreAsync(GitRecoveryEntry entry, CancellationToken token)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Folder));
        string parent = Path.GetDirectoryName(folder) ?? throw new IOException(Loc.Text("L4FDA10B98101"));
        CheckTarget(folder);
        if (!IsNetworkUrl(entry.Url) && !IsScpUrl(entry.Url) && !Path.IsPathFullyQualified(entry.Url))
            throw new IOException(Loc.Text("L551AC5A851E9"));

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        string temporary = Path.Combine(parent, StagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string[] branch = entry.Branch is null ? [] : ["--branch", entry.Branch];
            var (code, _, _) = await GitProjectOperations.RunAsync(parent,
                ["-c", "protocol.allow=never", "-c", "protocol.file.allow=always", "-c", "protocol.https.allow=always",
                 "-c", "protocol.http.allow=always", "-c", "protocol.ssh.allow=always", "-c", "protocol.git.allow=always",
                 "clone", "--no-local", "--origin", entry.Remote, .. branch, "--", entry.Url, temporary], token, 900);
            if (code != 0)
                throw new IOException(Loc.Text("L75413E2FA326"));

            var (commitCode, _, _) = await GitProjectOperations.RunAsync(temporary, ["rev-parse", "--verify", "HEAD"], token);
            if (commitCode != 0)
                throw new IOException(Loc.Text("LCCE8F475E8C8"));

            if (entry.Branch is not null)
            {
                var (headCode, head, _) = await GitProjectOperations.RunAsync(temporary, ["symbolic-ref", "--quiet", "HEAD"], token);
                if (headCode != 0 || head.TrimEnd('\r', '\n') != "refs/heads/" + entry.Branch)
                    throw new IOException(Loc.Text("L1B2156823EAC"));
            }

            token.ThrowIfCancellationRequested();
            CheckTarget(folder);
            Directory.Move(temporary, folder);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                // Git pack files can have the read-only attribute on Windows.
                foreach (var file in new DirectoryInfo(temporary).EnumerateFiles("*", new EnumerationOptions
                         { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    file.IsReadOnly = false;
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static void CheckTarget(string folder)
    {
        if (Exists(folder))
            throw new IOException(Loc.Text("L04AEDCE9C342"));
        for (string? current = Path.GetDirectoryName(folder); current is not null; current = Path.GetDirectoryName(current))
        {
            if (Exists(current) && (File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                != FileAttributes.Directory)
                throw new IOException(Loc.Text("LD203A9BD5FD4"));
        }
    }

    private static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static bool IsNetworkUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme is "https" or "http" or "ssh" or "git" || uri.IsFile && value.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    private static bool IsScpUrl(string value) => Regex.IsMatch(value, @"^[^:/\\\s]+:(?!:|//).+$") &&
        !Path.IsPathFullyQualified(value);

    private GitRecoveryEntry[] Load()
    {
        try
        {
            var entries = JsonSerializer.Deserialize<GitRecoveryEntry[]>(File.ReadAllText(filePath), JsonOptions);
            if (entries is null || entries.Any(entry => entry is null || !Path.IsPathFullyQualified(entry.Folder) ||
                    string.IsNullOrWhiteSpace(entry.Remote) || string.IsNullOrWhiteSpace(entry.Url)) ||
                entries.Select(entry => entry.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
                throw new JsonException();
            return entries;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException ex) { throw new IOException(Loc.Text("L7C7CE1C4019A"), ex); }
    }
}
