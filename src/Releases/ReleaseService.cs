using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace DevWorkspace;

internal sealed class ReleaseService(IReleaseProcess process, ReleaseStore store, Action<string> log)
{
    private const string VpkVersion = "1.2.161";
    private Task<string> Run(string tool, string[] args, string root, CancellationToken token, bool output = true) =>
        process.RunAsync(tool, args, root, output ? log : null, token);
    private Task<string> Gh(string[] args, CancellationToken token) => Run("gh", args, store.DirectoryPath, token, false);
    private Task<string> Api(string endpoint, CancellationToken token) => Gh(["api", "--hostname", "github.com", endpoint], token);

    internal async Task<string> RootAsync(string project, CancellationToken token)
    {
        string full = Path.GetFullPath(project);
        if (!File.Exists(full) || !full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new IOException(ReleaseText.T("ChooseProject"));
        return (await Run("git", ["rev-parse", "--show-toplevel"], Path.GetDirectoryName(full)!, token, false)).Trim();
    }
    private static string SafeProject(string root, string project)
    {
        string full = Path.GetFullPath(project);
        string relative = Path.GetRelativePath(root, full);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) &&
            full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return full;
        throw new IOException(ReleaseText.T("ProjectOutside"));
    }
    internal async Task<ReleaseProfile> DetectAsync(string project, CancellationToken token)
    {
        string root = await RootAsync(project, token);
        string origin = (await Run("git", ["remote", "get-url", "origin"], root, token, false)).Trim();
        var xml = XDocument.Load(project);
        string id = xml.Descendants("AssemblyName").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(project);
        string version = xml.Descendants("Version").FirstOrDefault()?.Value ?? "1.0.0";
        string tests = Path.Combine(root, "tests");
        string[] projects = Directory.Exists(tests) ? Directory.GetDirectories(tests)
            .SelectMany(d => Directory.GetFiles(d, "*.csproj")).ToArray() : [];
        return new(Path.GetFullPath(project), ReleaseRules.Repository(origin), id, version,
            TestProjects: string.Join(Environment.NewLine, projects.Select(p => Path.GetRelativePath(root, p))));
    }
    internal async Task<string> StatusAsync(string root, CancellationToken token) =>
        await Run("git", ["status", "--short", "--untracked-files=normal"], root, token, false);

    private async Task<string> CleanCommitAsync(string root, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(await StatusAsync(root, token))) throw new IOException(ReleaseText.T("Dirty"));
        return (await Run("git", ["rev-parse", "HEAD"], root, token, false)).Trim();
    }
    internal async Task CheckAsync(ReleaseProfile profile, CancellationToken token)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        ReleaseRules.Repository(profile.Repository);
        ReleaseRules.AppId(profile.AppId);
        ReleaseRules.Version(profile.Version);
        string root = await RootAsync(profile.ProjectFile, token);
        SafeProject(root, profile.ProjectFile);
        await VerifyOriginAsync(root, profile.Repository, token);
        await Run("dotnet", ["--version"], root, token);
        await Run("git", ["--version"], root, token);
        await Run("gh", ["--version"], root, token);
        string login = await Gh(["api", "user", "--jq", ".login"], token);
        log(ReleaseText.T("Account") + ": " + login.Trim());
        string status = await StatusAsync(root, token);
        log(string.IsNullOrWhiteSpace(status) ? ReleaseText.T("Clean") : status);
        string tracking = await Run("git", ["status", "--short", "--branch"], root, token, false);
        log(tracking);
        log(ReleaseText.T("CheckComplete"));
    }
    private async Task VerifyOriginAsync(string root, string repository, CancellationToken token)
    {
        string origin = (await Run("git", ["remote", "get-url", "origin"], root, token, false)).Trim();
        if (!ReleaseRules.Repository(origin).Equals(ReleaseRules.Repository(repository), StringComparison.OrdinalIgnoreCase))
            throw new IOException(ReleaseText.T("OriginMismatch"));
    }
    internal async Task CommitAsync(string root, string message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new IOException(ReleaseText.T("CommitMessage"));
        await Run("git", ["add", "--all"], root, token);
        await Run("git", ["commit", "-m", message], root, token);
    }
    internal async Task PushAsync(ReleaseProfile profile, CancellationToken token)
    {
        string root = await RootAsync(profile.ProjectFile, token);
        await VerifyOriginAsync(root, profile.Repository, token);
        string branch = (await Run("git", ["branch", "--show-current"], root, token, false)).Trim();
        if (branch.Length == 0) throw new IOException(ReleaseText.T("Detached"));
        await Run("git", ["push", "--set-upstream", "origin", $"HEAD:refs/heads/{branch}"], root, token);
    }
    internal async Task<string> NotesAsync(ReleaseProfile profile, string? previousTag, CancellationToken token)
    {
        string root = await RootAsync(profile.ProjectFile, token);
        string range = string.IsNullOrWhiteSpace(previousTag) ? "HEAD" : previousTag + "..HEAD";
        // End options before user/remote-supplied revisions; an unknown local tag is reported instead of silently changing scope.
        return await Run("git", ["log", "-40", "--format=- %s (%h)", "--end-of-options", range, "--"], root, token, false);
    }
    internal async Task<ReleasePackage> BuildAsync(ReleaseProfile profile, CancellationToken token)
    {
        ReleaseRules.Version(profile.Version);
        ReleaseRules.AppId(profile.AppId);
        ReleaseRules.Repository(profile.Repository);
        string root = await RootAsync(profile.ProjectFile, token);
        string project = SafeProject(root, profile.ProjectFile);
        await VerifyOriginAsync(root, profile.Repository, token);
        string commit = await CleanCommitAsync(root, token);
        string directory = store.NewPackageDirectory();
        Directory.CreateDirectory(directory);
        // Build the captured commit, even if an editor changes the working tree during compilation.
        string archive = Path.Combine(directory, "source.zip");
        await Run("git", ["archive", "--format=zip", "--output", archive, commit], root, token);
        string buildRoot = Path.Combine(directory, "source");
        await Task.Run(() => ZipFile.ExtractToDirectory(archive, buildRoot), token);
        string buildProject = SafeProject(buildRoot, Path.Combine(buildRoot, Path.GetRelativePath(root, project)));
        log(ReleaseText.T("Build") + " · " + profile.Version + " · " + commit + " · Debug / win-x64");
        await Run("dotnet", ["build", buildProject, "-c", "Debug"], buildRoot, token);
        foreach (string test in profile.TestProjects.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string originalTest = SafeProject(root, Path.Combine(root, test));
            string testPath = SafeProject(buildRoot, Path.Combine(buildRoot, Path.GetRelativePath(root, originalTest)));
            var testXml = XDocument.Load(testPath);
            bool executable = testXml.Descendants("OutputType").Any(n => n.Value.Equals("Exe", StringComparison.OrdinalIgnoreCase));
            await Run("dotnet", executable ? ["run", "--project", testPath, "-c", "Debug"] : ["test", testPath, "-c", "Debug"], buildRoot, token);
        }
        string publish = Path.Combine(directory, "publish");
        await Run("dotnet", ["publish", buildProject, "-c", "Debug", "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:Version=" + profile.Version, "-o", publish], buildRoot, token);
        string exeName = (await Run("dotnet", ["msbuild", buildProject, "-getProperty:AssemblyName", "-property:Configuration=Debug"], buildRoot, token, false)).Trim();
        string exe = exeName + ".exe";
        if (!File.Exists(Path.Combine(publish, exe))) throw new IOException(ReleaseText.T("MissingExe"));
        string tools = Path.Combine(store.DirectoryPath, "tools", VpkVersion);
        string vpk = Path.Combine(tools, "vpk.exe");
        if (!File.Exists(vpk))
            await Run("dotnet", ["tool", "install", "vpk", "--version", VpkVersion, "--tool-path", tools], root, token);
        var packArgs = new List<string> { "pack", "--packId", profile.AppId, "--packVersion", profile.Version, "--packDir", publish,
            "--mainExe", exe, "--packTitle", profile.AppId, "--channel", "win-x64", "--runtime", "win-x64",
            "--outputDir", Path.Combine(directory, "releases") };
        string? icon = XDocument.Load(buildProject).Descendants("ApplicationIcon").FirstOrDefault()?.Value;
        if (icon is not null && File.Exists(Path.Combine(Path.GetDirectoryName(buildProject)!, icon)))
            packArgs.AddRange(["--icon", Path.Combine(Path.GetDirectoryName(buildProject)!, icon)]);
        await Run(vpk, packArgs.ToArray(), buildRoot, token);
        var files = new List<ReleaseFile>();
        foreach (string file in System.IO.Directory.GetFiles(Path.Combine(directory, "releases")))
            if (Path.GetFileName(file) != "assets.win-x64.json") files.Add(await ReleaseStore.HashAsync(file, token));
        var package = new ReleasePackage(directory, root, project, ReleaseRules.Repository(profile.Repository), profile.AppId,
            profile.Version, commit, DateTimeOffset.UtcNow, files.ToArray());
        ReleaseRules.RequiredFiles(package);
        // Only complete, verified packages enter the resumable history.
        ReleaseStore.Save(Path.Combine(directory, "profile.json"), profile);
        await File.WriteAllTextAsync(Path.Combine(directory, "release-notes.md"), profile.Notes, token);
        ReleaseStore.Save(package.ManifestPath, package);
        log(ReleaseText.T("PackageReady") + " " + directory);
        return package;
    }

    internal static RemoteRelease ParseRelease(JsonElement r) => new(r.GetProperty("id").GetInt64(),
        r.GetProperty("tag_name").GetString()!, r.GetProperty("name").GetString() ?? "", r.GetProperty("target_commitish").GetString()!,
        r.GetProperty("draft").GetBoolean(), r.GetProperty("prerelease").GetBoolean(), r.GetProperty("html_url").GetString()!,
        r.GetProperty("published_at").ValueKind == JsonValueKind.String ? r.GetProperty("published_at").GetDateTimeOffset() : null,
        r.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset(a.GetProperty("id").GetInt64(),
            a.GetProperty("name").GetString()!, a.GetProperty("state").GetString()!, a.GetProperty("size").GetInt64(),
            a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            a.GetProperty("download_count").GetInt64(), a.GetProperty("browser_download_url").GetString()!)).ToArray());

    internal async Task<RemoteRelease[]> ReleasesAsync(string repository, CancellationToken token)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        string repo = ReleaseRules.Repository(repository);
        // gh emits one JSON object per line, preserving pagination without parsing concatenated arrays.
        string json = await Gh(["api", "--hostname", "github.com", "--paginate", $"repos/{repo}/releases?per_page=100",
            "--jq", ".[] | @json"], token);
        return json.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line =>
        { using var doc = JsonDocument.Parse(line); return ParseRelease(doc.RootElement); }).ToArray();
    }
    private async Task<RemoteRelease> ReadReleaseAsync(string repo, long id, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(await Api($"repos/{repo}/releases/{id}", token));
        return ParseRelease(doc.RootElement);
    }
    private async Task VerifyCommitAsync(ReleasePackage package, CancellationToken token)
    {
        string sha = (await Gh(["api", $"repos/{package.Repository}/commits/{package.Commit}", "--jq", ".sha"], token)).Trim();
        if (sha != package.Commit) throw new IOException(ReleaseText.T("PushRequired"));
    }
    private async Task VerifyTagAsync(ReleasePackage package, CancellationToken token)
    {
        string json;
        try { json = await Api($"repos/{package.Repository}/git/ref/tags/{package.Tag}", token); }
        catch (IOException ex) when (ex.Message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)) { return; }
        using var reference = JsonDocument.Parse(json);
        var target = reference.RootElement.GetProperty("object").Clone();
        for (int i = 0; target.GetProperty("type").GetString() == "tag" && i < 8; i++)
        {
            using var tag = JsonDocument.Parse(await Api($"repos/{package.Repository}/git/tags/{target.GetProperty("sha").GetString()}", token));
            target = tag.RootElement.GetProperty("object").Clone();
        }
        if (target.GetProperty("type").GetString() != "commit" || target.GetProperty("sha").GetString() != package.Commit)
            throw new IOException(ReleaseText.T("DraftMismatch"));
    }
    internal async Task UploadDraftAsync(ReleasePackage package, string notes, CancellationToken token)
    {
        await ReleaseStore.VerifyAsync(package, token);
        await VerifyCommitAsync(package, token);
        await VerifyTagAsync(package, token);
        var release = (await ReleasesAsync(package.Repository, token)).SingleOrDefault(r => r.Tag == package.Tag);
        string notesPath = Path.Combine(package.Directory, "release-notes.md");
        await File.WriteAllTextAsync(notesPath, notes, token);
        if (release is null)
        {
            await Gh(["release", "create", package.Tag, "--repo", package.Repository, "--target", package.Commit,
                "--title", package.AppId + " " + package.Version, "--notes-file", notesPath, "--draft"], token);
            release = (await ReleasesAsync(package.Repository, token)).Single(r => r.Tag == package.Tag);
        }
        ReleaseRules.SameDraft(package, release);
        await Gh(["release", "edit", package.Tag, "--repo", package.Repository, "--notes-file", notesPath], token);
        foreach (var file in package.Files)
        {
            token.ThrowIfCancellationRequested();
            release = await ReadReleaseAsync(package.Repository, release.Id, token);
            ReleaseRules.SameDraft(package, release);
            var existing = release.Assets.SingleOrDefault(a => a.Name == file.Name);
            if (existing is not null)
            {
                if (existing.State == "starter" && existing.Size == 0)
                {
                    // GitHub documents empty 'starter' assets after failed uploads. Only this draft-owned empty entry is retried.
                    log(ReleaseText.T("RetryEmpty") + " " + file.Name);
                    await Gh(["api", "--method", "DELETE", $"repos/{package.Repository}/releases/assets/{existing.Id}"], token);
                }
                else
                {
                    // Never delete/overwrite a mismatched asset or touch an already published release.
                    ReleaseRules.MatchingAsset(file, existing);
                    log(ReleaseText.T("AlreadyUploaded") + " " + file.Name);
                    continue;
                }
            }
            log(ReleaseText.T("Uploading") + " " + file.Name);
            await Gh(["release", "upload", package.Tag, Path.Combine(package.Directory, "releases", file.Name),
                "--repo", package.Repository], token);
            release = await ReadReleaseAsync(package.Repository, release.Id, token);
            ReleaseRules.MatchingAsset(file, release.Assets.Single(a => a.Name == file.Name));
        }
        await VerifyRemoteAsync(package, token);
        log(ReleaseText.T("DraftReady"));
    }
    internal async Task<RemoteRelease> VerifyRemoteAsync(ReleasePackage package, CancellationToken token)
    {
        await ReleaseStore.VerifyAsync(package, token);
        var release = (await ReleasesAsync(package.Repository, token)).SingleOrDefault(r => r.Tag == package.Tag)
            ?? throw new IOException(ReleaseText.T("NoDraft"));
        ReleaseRules.SameDraft(package, release);
        if (release.Assets.Length != package.Files.Length) throw new IOException(ReleaseText.T("IncompletePackage"));
        foreach (var file in package.Files)
            ReleaseRules.MatchingAsset(file, release.Assets.SingleOrDefault(a => a.Name == file.Name)
                ?? throw new IOException(ReleaseText.T("IncompletePackage")));
        await VerifyCommitAsync(package, token);
        await VerifyTagAsync(package, token);
        log(ReleaseText.T("RemoteVerified"));
        return release;
    }
    internal async Task<string> ChecksAsync(string repo, string commit, CancellationToken token)
    {
        string output = await Gh(["api", "--paginate", $"repos/{ReleaseRules.Repository(repo)}/actions/runs?head_sha={Uri.EscapeDataString(commit)}&per_page=100",
            "--jq", ".workflow_runs[] | [.name, .status, (.conclusion // \"pending\"), .html_url] | @tsv"], token);
        log(output.Length == 0 ? ReleaseText.T("NoChecks") : output);
        return output;
    }
    internal async Task PublishAsync(ReleasePackage package, bool requireChecks, CancellationToken token, string? notes = null)
    {
        await VerifyRemoteAsync(package, token);
        if (requireChecks)
        {
            string checks = await ChecksAsync(package.Repository, package.Commit, token);
            if (string.IsNullOrWhiteSpace(checks) || checks.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
                { var parts = line.TrimEnd('\r').Split('\t'); return parts.Length < 3 || parts[1] != "completed" || parts[2] != "success"; }))
                throw new IOException(ReleaseText.T("ChecksRequired"));
        }
        // Stable versions only: avoid replacing the update feed's latest pointer with an older version.
        var releases = await ReleasesAsync(package.Repository, token);
        if (releases.Where(r => !r.Draft && !r.Prerelease).Any(r =>
            System.Version.TryParse(r.Tag.TrimStart('v'), out var v) && v >= System.Version.Parse(package.Version)))
            throw new IOException(ReleaseText.T("OlderVersion"));
        var publishArgs = new List<string> { "release", "edit", package.Tag, "--repo", package.Repository, "--draft=false", "--latest" };
        if (notes is not null)
        {
            string notesPath = Path.Combine(package.Directory, "release-notes.md");
            await File.WriteAllTextAsync(notesPath, notes, token);
            publishArgs.AddRange(["--notes-file", notesPath]);
        }
        await Gh(publishArgs.ToArray(), token);
        log(ReleaseText.T("Published") + " https://github.com/" + package.Repository + "/releases/tag/" + package.Tag);
    }
    internal async Task<string> RepositoryStatsAsync(string repo, CancellationToken token) =>
        await Gh(["api", $"repos/{ReleaseRules.Repository(repo)}", "--jq", "[.stargazers_count, .forks_count, .open_issues_count] | @tsv"], token);

}
