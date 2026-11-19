using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevWorkspace;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
// Explicit integration commands exercise the same service used by the WPF window; the default suite uses no network.
if (args is ["--build-live", var liveProject, var liveVersion, var liveNotes])
{
    var liveStore = new ReleaseStore();
    Directory.CreateDirectory(liveStore.DirectoryPath);
    var liveService = new ReleaseService(new ReleaseProcess(), liveStore, Console.WriteLine);
    var liveProfile = (await liveService.DetectAsync(liveProject, default)) with { Version = liveVersion, Notes = File.ReadAllText(liveNotes) };
    liveStore.SaveProfile(liveProfile);
    var livePackage = await liveService.BuildAsync(liveProfile, default);
    Console.WriteLine("MANIFEST: " + livePackage.ManifestPath);
    return;
}
if (args is ["--upload-live" or "--publish-live", var liveManifest, var notesFile])
{
    var liveStore = new ReleaseStore();
    var liveService = new ReleaseService(new ReleaseProcess(), liveStore, Console.WriteLine);
    var livePackage = JsonSerializer.Deserialize<ReleasePackage>(File.ReadAllText(liveManifest), ReleaseStore.Json)!;
    if (args[0] == "--upload-live") await liveService.UploadDraftAsync(livePackage, File.ReadAllText(notesFile), default);
    else await liveService.PublishAsync(livePackage, true, default);
    return;
}
if (args is ["--wait"]) { await Task.Delay(60000); return; }
if (args is ["--echo", var argument]) { Console.WriteLine(argument); return; }
if (args is ["--fail"]) { Console.Error.WriteLine("expected failure"); Environment.ExitCode = 7; return; }

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
async Task Reject(Func<Task> action, string name)
{
    bool rejected = false;
    try { await action(); } catch (IOException) { rejected = true; }
    Check(rejected, name);
}
void Bad(Action action, string name) => Reject(() => { action(); return Task.CompletedTask; }, name).GetAwaiter().GetResult();

foreach (string version in new[] { "1.0.6", "0.0.0", "123.45.67" }) { ReleaseRules.Version(version); checks++; }
foreach (string version in new[] { "01.0.0", "1.2", "1.2.3.4", "1.2.3-beta", "-1.2.3", "1.2.3\n", "1.0.999999999999999" })
    Bad(() => ReleaseRules.Version(version), "Invalid version: " + version);
foreach (string repo in new[] { "owner/repo", "https://github.com/owner/repo.git", "git@github.com:owner/repo.git" })
    Check(ReleaseRules.Repository(repo) == "owner/repo", "Repository normalization");
foreach (string repo in new[] { "--help", "a/b?token=x", "a/../b", "https://evil.test/a/b", "a/b\n--delete", "a/." })
    Bad(() => ReleaseRules.Repository(repo), "Unsafe repository: " + repo);
Bad(() => ReleaseRules.AppId("../App"), "Unsafe app ID");
Check(ReleaseRules.Csv("=1+1") == "\"'=1+1\"", "CSV formula escaping");
Check(ReleaseRules.Csv("a,\"b") == "\"a,\"\"b\"", "CSV quote escaping");
Check(!ReleaseRules.IsDownload("releases.win-x64.json") && ReleaseRules.IsDownload("App-win-x64-Setup.exe"), "Feed excluded from downloads");

string directory = Path.Combine(Path.GetTempPath(), "release-center-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var store = new ReleaseStore(Path.Combine(directory, "state"));
    Directory.CreateDirectory(store.DirectoryPath);
    string source = Path.Combine(directory, "repo with spaces");
    Directory.CreateDirectory(source);
    string project = Path.Combine(source, "App.csproj");
    File.WriteAllText(project, "<Project><PropertyGroup><Version>1.0.5</Version><OutputType>WinExe</OutputType></PropertyGroup></Project>");
    var profile = new ReleaseProfile(project, "owner/repo", "App", "1.0.6", "line 1\n`$literal` line 2");
    store.SaveProfile(profile);
    Check(store.LoadProfile() == profile, "Profile round trip preserves multiline notes");
    var fake = new FakeProcess(source);
    var service = new ReleaseService(fake, store, _ => { });

    var detected = await service.DetectAsync(project, default);
    Check(detected.Repository == "owner/repo" && detected.AppId == "App" && detected.Version == "1.0.5", "Detect origin, ID and version");
    fake.Dirty = true;
    await Reject(() => service.BuildAsync(profile, default), "Dirty source must fail before building");
    Check(!fake.Calls.Any(c => c.Tool == "dotnet"), "Dirty build started no compiler");
    fake.Dirty = false;
    var package = await service.BuildAsync(profile, default);
    Check(store.Packages().Single().Commit == fake.Commit, "Package history captures source commit");
    Check(fake.Calls.Any(c => c.Tool == "git" && c.Args[0] == "archive"), "Build uses an isolated commit snapshot");
    var publish = fake.Calls.Single(c => c.Tool == "dotnet" && c.Args[0] == "publish");
    Check(publish.Args.Contains("Debug") && !publish.Args.Contains("Release") &&
        publish.Args.Contains("-p:IncludeNativeLibrariesForSelfExtract=true") && publish.Args.Contains("-p:Version=1.0.6"), "Debug single-file native publish flags");
    Check(publish.Directory != source && publish.Directory.StartsWith(store.DirectoryPath), "Compilation uses the snapshot directory");
    await ReleaseStore.VerifyAsync(package, default);
    checks++;
    Bad(() => ReleaseRules.RequiredFiles(package with { Files = package.Files[..^1] }), "Incomplete package rejected");
    Bad(() => ReleaseRules.RequiredFiles(package with { Files = [..package.Files[..^1], package.Files[^1] with { Name = "../bad" }] }), "Traversal asset rejected");
    string assetPath = Path.Combine(package.Directory, "releases", package.Files[0].Name);
    string content = File.ReadAllText(assetPath);
    File.WriteAllText(assetPath, "tampered");
    await Reject(() => service.UploadDraftAsync(package, profile.Notes, default), "Tampered local file rejected");
    Check(fake.Release is null, "Tampering created no remote draft");
    File.WriteAllText(assetPath, content);

    fake.OmitOwnReleaseFromList = true;
    fake.FailUploadOnce = package.Files[1].Name;
    await Reject(() => service.UploadDraftAsync(package, profile.Notes, default), "Simulated interrupted upload");
    Check(fake.Release is { Draft: true, Assets.Length: 1 }, "Interrupted upload preserves a recoverable draft");
    Check(File.Exists(Path.Combine(package.Directory, "github-draft.json")), "Created draft identity is persisted for retries");
    int firstUploads = fake.Uploads[package.Files[0].Name];
    await service.UploadDraftAsync(package, profile.Notes, default);
    Check(fake.Uploads[package.Files[0].Name] == firstUploads && fake.Release?.Assets.Length == 5, "Resume skips previously verified files");
    Check(fake.OmitOwnReleaseFromList && fake.Release?.Assets.Length == 5, "Upload and resume succeed while GitHub's release list omits the new draft");
    Check(File.ReadAllText(Path.Combine(package.Directory, "release-notes.md")) == profile.Notes, "Release notes retain exact literal text");
    Check(fake.Calls.All(c => !c.Args.Contains("--clobber") && !c.Args.Contains("--force")), "Upload never overwrites assets or force pushes");
    var remote = fake.Release!;
    fake.Release = remote with { Assets = [remote.Assets[0] with { Digest = "sha256:wrong" }, ..remote.Assets[1..]] };
    int mutationCount = fake.Calls.Count(c => c.Args.Contains("upload"));
    await Reject(() => service.UploadDraftAsync(package, profile.Notes, default), "Mismatched remote asset rejected");
    Check(fake.Calls.Count(c => c.Args.Contains("upload")) == mutationCount, "Mismatch does not replace an uploaded asset");
    fake.Release = remote with { Target = new string('b', 40) };
    await Reject(() => service.UploadDraftAsync(package, profile.Notes, default), "Wrong commit draft rejected");
    fake.Release = remote with { Draft = false };
    int edits = fake.Calls.Count(c => c.Args.Contains("edit"));
    await Reject(() => service.UploadDraftAsync(package, profile.Notes, default), "Published release cannot be modified");
    Check(edits == fake.Calls.Count(c => c.Args.Contains("edit")), "Published release untouched");
    fake.Release = remote;
    fake.CheckStatus = "CI\tin_progress\tpending\thttps://github.com/owner/repo/actions/runs/1\n";
    await Reject(() => service.PublishAsync(package, true, default), "Pending CI prevents publication");
    Check(fake.Release.Draft, "Pending CI leaves draft private");
    fake.CheckStatus = "CI\tcompleted\tfailure\turl\n";
    await Reject(() => service.PublishAsync(package, true, default), "Failed CI prevents publication");
    fake.CheckStatus = "";
    await Reject(() => service.PublishAsync(package, true, default), "Missing CI requires explicit opt out");
    fake.CheckStatus = "CI\tcompleted\tsuccess\turl\n";
    fake.ExtraRelease = remote with { Id = 9, Tag = "v1.0.7", Draft = false };
    await Reject(() => service.PublishAsync(package, true, default), "Older version cannot replace latest");
    fake.ExtraRelease = null;
    await service.PublishAsync(package, true, default, "updated notes\nsecond line");
    Check(fake.Release is { Draft: false }, "Verified complete package publishes after successful CI");
    Check(fake.Calls.Last(c => c.Args.Contains("--draft=false")).Args.Contains("--notes-file") &&
        File.ReadAllText(Path.Combine(package.Directory, "release-notes.md")) == "updated notes\nsecond line", "Publication includes the latest edited multiline notes");

    fake.Release = remote;
    fake.TagCommit = new string('c', 40);
    await Reject(() => service.PublishAsync(package, false, default), "Retargeted tag blocks publication even if the release target is unchanged");
    fake.TagCommit = null;
    fake.RemoteCommitExists = false;
    await Reject(() => service.PublishAsync(package, false, default), "Missing remote source commit blocks publication");
    fake.RemoteCommitExists = true;
    fake.CheckStatus = "";
    await service.PublishAsync(package, false, default);
    Check(fake.Release is { Draft: false }, "Explicit CI opt out supports repositories without workflows");

    fake.Release = remote with { Assets = [..remote.Assets[..^1], remote.Assets[^1] with { State = "starter", Size = 0, Digest = null }] };
    await service.UploadDraftAsync(package, profile.Notes, default);
    Check(fake.Release.Assets.All(a => a.State == "uploaded"), "Empty interrupted starter assets can be resumed");

    var downloads = remote with { Assets = [new(1, "App-win-x64-Setup.exe", "uploaded", 10, null, 4, ""),
        new(2, "App-win-x64-Portable.zip", "uploaded", 10, null, 3, ""), new(3, "App-1.0.6-win-x64-full.nupkg", "uploaded", 10, null, 2, ""),
        new(4, "releases.win-x64.json", "uploaded", 10, null, 99, "")] };
    Check(downloads.Downloads == 9, "Asset statistics exclude feed requests");

    // Exercise the real process adapter: Unicode and shell metacharacters remain one literal argument.
    var real = new ReleaseProcess();
    string literal = "şğ test & | ` $() \"quote\"";
    string output = await real.RunAsync(Environment.ProcessPath!, ["--echo", literal], directory, null, default);
    Check(output.Trim() == literal, "Process arguments remain literal, including Unicode");
    await Reject(() => real.RunAsync(Environment.ProcessPath!, ["--fail"], directory, null, default), "Nonzero process exit propagates");
    using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var elapsed = Stopwatch.StartNew();
    bool stopped = false;
    try { await real.RunAsync(Environment.ProcessPath!, ["--wait"], directory, null, canceled.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Check(stopped && elapsed.Elapsed < TimeSpan.FromSeconds(10), "Cancellation terminates the running process");

    string src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
    foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Where(p =>
        !p.Contains("\\obj\\") && !p.Contains("\\bin\\") && (p.EndsWith(".cs") || p.EndsWith(".xaml"))))
    {
        string text = File.ReadAllText(file);
        foreach (Match match in Regex.Matches(text, "ReleaseText\\.(?:T|F)\\(\"(?<key>[^\"]+)\"|DynamicResource Release(?<key>[A-Za-z]+)"))
            Check(ReleaseText.Messages.ContainsKey(match.Groups["key"].Value), "Missing release translation: " + match.Groups["key"].Value);
    }
    foreach (var (_, translations) in ReleaseText.Messages)
    {
        Check(!string.IsNullOrWhiteSpace(translations.Tr) && !string.IsNullOrWhiteSpace(translations.En), "Both languages exist");
        Check(System.Text.CompositeFormat.Parse(translations.Tr).MinimumArgumentCount ==
            System.Text.CompositeFormat.Parse(translations.En).MinimumArgumentCount, "Translation format placeholders match");
    }
    Console.WriteLine($"{checks} release center checks passed: isolated Debug builds, provenance, integrity, resumable uploads, publish guards, statistics, process cancellation and translations.");
}
finally { Directory.Delete(directory, true); }

sealed class FakeProcess(string root) : IReleaseProcess
{
    public string Commit = new('a', 40);
    public bool Dirty;
    public bool RemoteCommitExists = true;
    public string CheckStatus = "";
    public string? TagCommit;
    public bool OmitOwnReleaseFromList;
    public string? FailUploadOnce;
    public RemoteRelease? Release;
    public RemoteRelease? ExtraRelease;
    public readonly List<(string Tool, string[] Args, string Directory)> Calls = [];
    public readonly Dictionary<string, int> Uploads = [];
    static string Json(RemoteRelease r) => JsonSerializer.Serialize(new
    {
        id = r.Id, tag_name = r.Tag, name = r.Name, target_commitish = r.Target, draft = r.Draft,
        prerelease = r.Prerelease, html_url = r.Url, published_at = r.Published,
        assets = r.Assets.Select(a => new { id = a.Id, name = a.Name, state = a.State, size = a.Size,
            digest = a.Digest, download_count = a.Downloads, browser_download_url = a.Url })
    });
    public async Task<string> RunAsync(string tool, string[] a, string directory, Action<string>? log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Calls.Add((tool, a, directory));
        string Get(string flag) => a[Array.IndexOf(a, flag) + 1];
        if (tool == "git")
        {
            if (a[0] == "rev-parse") return a[1] == "HEAD" ? Commit : root;
            if (a[0] == "remote") return "https://github.com/owner/repo.git";
            if (a[0] == "status") return Dirty ? " M App.csproj" : "";
            if (a[0] == "ls-remote") return "";
            if (a[0] == "archive")
            {
                using var archive = ZipFile.Open(Get("--output"), ZipArchiveMode.Create);
                archive.CreateEntryFromFile(Path.Combine(root, "App.csproj"), "App.csproj");
                return "";
            }
        }
        if (tool == "dotnet")
        {
            if (a[0] == "publish") { Directory.CreateDirectory(Get("-o")); File.WriteAllText(Path.Combine(Get("-o"), "App.exe"), "exe"); }
            if (a[0] == "msbuild") return "App";
            return "";
        }
        if (tool.EndsWith("vpk.exe"))
        {
            string output = Get("--outputDir"); Directory.CreateDirectory(output);
            foreach (string name in new[] { "App-win-x64-Setup.exe", "App-win-x64-Portable.zip", "App-1.0.6-win-x64-full.nupkg", "releases.win-x64.json", "RELEASES-win-x64" })
                File.WriteAllText(Path.Combine(output, name), "original content " + name);
            return "";
        }
        if (tool == "gh")
        {
            if (a[0] == "api")
            {
                string endpoint = a.FirstOrDefault(v => v.StartsWith("repos/")) ?? "";
                if (a.Contains("POST") && endpoint.EndsWith("/releases"))
                {
                    using var input = JsonDocument.Parse(File.ReadAllText(Get("--input")));
                    string tag = input.RootElement.GetProperty("tag_name").GetString()!;
                    string target = input.RootElement.GetProperty("target_commitish").GetString()!;
                    Release = new(1, tag, "App 1.0.6", target, true, false, "https://github.com/owner/repo/releases/tag/" + tag, null, []);
                    return Json(Release);
                }
                if (endpoint.Contains("/git/ref/tags/"))
                    return TagCommit is null ? throw new IOException("HTTP 404") : JsonSerializer.Serialize(new { @object = new { type = "commit", sha = TagCommit } });
                if (a.Contains("DELETE"))
                {
                    long id = long.Parse(endpoint.Split('/')[^1]);
                    Release = Release! with { Assets = Release!.Assets.Where(asset => asset.Id != id).ToArray() };
                    return "";
                }
                if (endpoint.Contains("/commits/")) return RemoteCommitExists ? Commit : throw new IOException("not pushed");
                if (endpoint.Contains("/actions/runs")) return CheckStatus;
                if (endpoint.Contains("releases?")) return string.Join('\n', new[] { OmitOwnReleaseFromList ? null : Release, ExtraRelease }.OfType<RemoteRelease>().Select(Json));
                if (endpoint.Split('?')[0].EndsWith("releases/1")) return Json(Release!);
            }
            if (a[0] == "release")
            {
                if (a[1] == "create") Release = new(1, a[2], "App 1.0.6", Get("--target"), true, false, "https://github.com/owner/repo/releases/tag/" + a[2], null, []);
                if (a[1] == "upload")
                {
                    string path = a[3], name = Path.GetFileName(path);
                    if (FailUploadOnce == name) { FailUploadOnce = null; throw new IOException("simulated network failure"); }
                    var file = await ReleaseStore.HashAsync(path, token);
                    Uploads[name] = Uploads.GetValueOrDefault(name) + 1;
                    Release = Release! with { Assets = [..Release!.Assets, new(Release.Assets.Length + 1, name, "uploaded", file.Size, "sha256:" + file.Sha256, 0, "https://github.com/file")] };
                }
                if (a[1] == "edit" && a.Contains("--draft=false")) Release = Release! with { Draft = false };
                return "";
            }
        }
        return "";
    }
}
