using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DevWorkspace;

internal static class ProductivityChecks
{
    private static ProjectEntry Project(string key, string folder, string? entry = null, string? repo = null) =>
        new(key, "", key, folder, entry, "", repo, new(GitState.None));
    private static async Task Git(string folder, params string[] args)
    {
        var result = await GitProjectOperations.RunAsync(folder, args, CancellationToken.None);
        if (result.Code != 0) throw new Exception(result.Error + result.Output);
    }
    private static async Task Init(string folder)
    {
        Directory.CreateDirectory(folder); await Git(folder, "init", "-b", "main");
        await Git(folder, "config", "user.name", "Productivity tests"); await Git(folder, "config", "user.email", "tests@example.invalid");
        File.WriteAllText(Path.Combine(folder, "source.txt"), "initial"); await Git(folder, "add", "."); await Git(folder, "commit", "-m", "initial");
        await Git(folder, "branch", "feature");
    }
    private static async Task<bool> Reject(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { return true; }
    }

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string fixture = Path.Combine(root, "productivity"); Directory.CreateDirectory(fixture);
        await WorkSetChecks(fixture, check); await EnvironmentChecks(fixture, check);
        await DiagnosticChecks(fixture, check); ImpactChecks(fixture, check);
        await ResourceChecks(fixture, check); await CleanupChecks(fixture, check);
    }

    private static async Task WorkSetChecks(string root, Action<bool, string> check)
    {
        string first = Path.Combine(root, "repo-first"), second = Path.Combine(root, "repo-second");
        await Init(first); await Init(second);
        var projects = new[] { Project("first", first, repo: first), Project("second", second, repo: second) };
        var session = new WorkspaceSession("first", [], ResumeNotes: new() { ["first"] = new("context", "next", DateTime.UtcNow) });
        var saved = await WorkSets.CaptureAsync(Guid.NewGuid(), "Payment fix", "Investigate rounding", "Local", projects, session, CancellationToken.None);
        check(saved.Projects.All(p => p.Branch == "main") && saved.Projects[0].Note?.Next == "next", "Work sets capture fresh branches and project notes");
        await Git(first, "switch", "feature"); await Git(second, "switch", "feature");
        File.WriteAllText(Path.Combine(second, "source.txt"), "unsaved");
        check(await Reject(() => WorkSets.RestoreBranchesAsync(saved, projects, CancellationToken.None)), "Dirty branch switches block work set restore");
        check((await GitStatus.ReadAsync(first, CancellationToken.None)).Branch == "feature", "All repositories are checked before any branch changes");
        check(File.ReadAllText(Path.Combine(second, "source.txt")) == "unsaved", "Work sets preserve uncommitted edits");
        await Git(second, "restore", "source.txt");
        await Git(second, "branch", "-D", "main");
        check(await Reject(() => WorkSets.RestoreBranchesAsync(saved, projects, CancellationToken.None)), "Deleted work set branch is rejected before switching other repositories");
        check((await GitStatus.ReadAsync(first, CancellationToken.None)).Branch == "feature", "A missing later branch leaves earlier branches intact");
        await Git(second, "branch", "main");
        string tree = Path.Combine(root, "worktree"); await Git(second, "worktree", "add", tree, "main");
        check(await Reject(() => WorkSets.RestoreBranchesAsync(saved, projects, CancellationToken.None)), "A branch checked out in another worktree is rejected");
        await Git(second, "worktree", "remove", tree);
        await WorkSets.RestoreBranchesAsync(saved, projects, CancellationToken.None);
        check((await GitStatus.ReadAsync(first, CancellationToken.None)).Branch == "main" && (await GitStatus.ReadAsync(second, CancellationToken.None)).Branch == "main", "Work set restores multiple repository branches");
        await Git(first, "switch", "--detach");
        var detached = await WorkSets.CaptureAsync(Guid.NewGuid(), "Detached investigation", "", null, [projects[0]], session, CancellationToken.None);
        check(detached.Projects[0].Branch is null && detached.Projects[0].Commit?.Length >= 40, "Detached worktrees are captured by commit");
        await Git(first, "switch", "feature"); await WorkSets.RestoreBranchesAsync(detached, projects, CancellationToken.None);
        var head = await GitProjectOperations.RunAsync(first, ["symbolic-ref", "--quiet", "HEAD"], CancellationToken.None);
        check(head.Code != 0, "Detached work sets restore detached state");
        await Git(first, "switch", "-c", "with-env"); File.WriteAllText(Path.Combine(first, "settings.local"), "tracked branch value");
        await Git(first, "add", "settings.local"); await Git(first, "commit", "-m", "branch-specific environment");
        var withEnvironment = await WorkSets.CaptureAsync(Guid.NewGuid(), "With environment", "", null, [projects[1], projects[0]], session, CancellationToken.None);
        await Git(first, "switch", "main"); await Git(second, "switch", "feature");
        File.AppendAllText(Path.Combine(first, ".git", "info", "exclude"), "\nsettings.local\n");
        File.WriteAllText(Path.Combine(first, "settings.local"), "private local value");
        check(await Reject(() => WorkSets.RestoreBranchesAsync(withEnvironment, projects, CancellationToken.None)), "Work set switches do not overwrite ignored local environment files");
        check(File.ReadAllText(Path.Combine(first, "settings.local")) == "private local value" && (await GitStatus.ReadAsync(second, CancellationToken.None)).Branch == "feature", "A later switch failure preserves local files and rolls earlier repositories back");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        check(await Reject(() => WorkSets.RestoreBranchesAsync(saved, projects, canceled.Token)), "Canceled work set restore does not switch branches");
        var state = new SessionState(); state.Workspaces[root] = session with { WorkSets = [saved], LastWorkSet = saved.Id };
        string path = Path.Combine(root, "work-sets.json"); state.SaveTo(path);
        var loaded = SessionState.LoadFrom(path).For(root);
        check(loaded.WorkSets is { Length: 1 } && loaded.LastWorkSet == saved.Id && loaded.WorkSets[0].Projects[0].Note?.Context == "context", "Work sets survive application restart");
    }

    private static async Task EnvironmentChecks(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "environment"); Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, ".env.local");
        File.WriteAllText(file, "# local\nexport TEST_VALUE=\"value with spaces\"\nEMPTY=\nLITERAL='$HOME'\nURL=http://localhost:5100 # local\n");
        File.WriteAllText(Path.Combine(folder, ".env"), "ORIGINAL=keep");
        var values = LaunchEnvironment.Read(file);
        check(values["TEST_VALUE"] == "value with spaces" && values["EMPTY"] == "" && values["LITERAL"] == "$HOME" && values["URL"] == "http://localhost:5100", "Environment parser handles quotes, comments and literal values without shell expansion");
        var project = Project("environment", folder);
        var profile = new LaunchProfile("Default", [new("environment", new("Read environment", Environment.ProcessPath!, "--echo-env TEST_VALUE"), WaitForExit: true)]);
        var variant = LaunchEnvironment.Variant(profile, "Local", "Local", project, ".env.local", null, false);
        using (var run = new DevelopmentRun(CancellationToken.None))
        {
            await run.StartAsync(variant, [project], _ => Task.CompletedTask, _ => { }, _ => { });
            var operation = OperationCenter.Entries.First(o => o.Name == "Read environment");
            check(!operation.IsFailed && operation.ReadOutput().Contains("value with spaces"), "Environment values reach only the launched child process");
        }
        check(Environment.GetEnvironmentVariable("TEST_VALUE") is null && File.ReadAllText(Path.Combine(folder, ".env")) == "ORIGINAL=keep", "Environment variants preserve parent and on-disk .env values");
        var info = ProjectTools.CreateCaptureInfo("cmd.exe", folder, [], values);
        check(info.Environment["TEST_VALUE"] == "value with spaces", "Captured inspection commands share the same environment as launched services");
        string outside = Path.Combine(root, "environment-outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, ".env"), "TEST_VALUE=outside");
        string link = Path.Combine(folder, "linked");
        int linked = await ProjectTools.RunAsync(new("Environment junction", "cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + outside + "\""), folder, _ => { }, CancellationToken.None);
        if (linked != 0) throw new Exception("Could not create environment junction fixture");
        check(await Reject(() => { LaunchEnvironment.Resolve(folder, "linked/.env"); return Task.CompletedTask; }), "Environment paths cannot escape through junctions");
        Directory.Delete(link);
        foreach (string invalid in new[] { "bad line", "DEVWORKSPACE_PROJECT_TOOL=bad", "KEY=\"unterminated" })
        {
            string bad = Path.Combine(folder, "bad.env"); File.WriteAllText(bad, invalid);
            check(await Reject(() => { LaunchEnvironment.Read(bad); return Task.CompletedTask; }), "Invalid or reserved environment input rejected");
        }
        foreach (string invalid in new[] { "../outside.env", "C:/outside.env", "..\\outside.env" })
            check(await Reject(() => { LaunchEnvironment.Resolve(folder, invalid); return Task.CompletedTask; }), "Environment file cannot escape the project");
        File.WriteAllText(Path.Combine(folder, "package.json"), "{\"scripts\":{\"dev\":\"node app.js\"}}");
        project = project with { EntryFile = Path.Combine(folder, "package.json") };
        var preset = ProjectTools.Presets(project).First();
        var portable = new LaunchProfile("Local web", [new("environment", preset)], "Local", new() { ["environment"] = ".env.local" });
        string shared = WorkspaceSharing.ExportProfile(portable, [project], root);
        var imported = WorkspaceSharing.ImportProfile(shared, [project], root);
        check(imported.EnvironmentName == "Local" && imported.EnvironmentFiles?["environment"] == ".env.local" && !shared.Contains("value with spaces"), "Preset sharing preserves file paths without exporting local values");
        string team = DevelopmentConfigurationFile.Export([portable], root);
        check(DevelopmentConfigurationFile.Import(team, root).Profiles[0].EnvironmentFiles?["environment"] == ".env.local" && !team.Contains("value with spaces"), "Team configurations preserve environment variants without secret contents");
        var narrowed = LaunchEnvironment.Variant(portable with { Steps = [.. portable.Steps, new("other", null)] }, "Frontend only", "Frontend", project, null, 5173, true);
        check(narrowed.Steps.Length == 1 && narrowed.Steps[0].ReadyPort == 5173, "Environment variants narrow project scope and override readiness port");
        var buildThenRun = portable with { Steps = [new("environment", new("Build", "cmd.exe", "/d /c exit 0"), WaitForExit: true), .. portable.Steps] };
        var portVariant = LaunchEnvironment.Variant(buildThenRun, "Local port", "Local", project, null, 5174, false);
        check(portVariant.Steps[0].WaitForExit && portVariant.Steps[0].ReadyPort is null && portVariant.Steps[1].ReadyPort == 5174, "Port overrides preserve build tasks and only update the service step");
    }

    private static async Task DiagnosticChecks(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "diagnostics"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "package.json"), "{}"); File.WriteAllText(Path.Combine(folder, "package-lock.json"), "{}");
        var project = Project("diagnostics", folder, Path.Combine(folder, "App.csproj"));
        check(DiagnosticSolutions.Analyze("Error EADDRINUSE: address already in use 0.0.0.0:5173", project).Single().Port == 5173, "Port diagnosis extracts the conflicting port");
        check(DiagnosticSolutions.Analyze("NETSDK1045: current SDK does not support target", project).Any(s => s.Tool == DevelopmentTool.DotNet), "SDK errors route to SDK inspection and setup");
        check(DiagnosticSolutions.Analyze("NETSDK1004: project.assets.json missing", project).Single().Fix?.Arguments.StartsWith("restore") == true, "Missing NuGet assets suggest restore");
        check(DiagnosticSolutions.Analyze("Cannot find module vite", project).Single().Fix?.Arguments == "/d /c npm.cmd ci", "Dependency diagnosis uses npm lockfile");
        File.WriteAllText(Path.Combine(folder, "pnpm-lock.yaml"), "lockfileVersion: 9");
        check(DiagnosticSolutions.Analyze("Cannot find module vite", project).Single().Fix?.Arguments.Contains("pnpm.cmd") == true, "Dependency diagnosis respects pnpm lockfile");
        File.WriteAllText(Path.Combine(folder, "compose.yaml"), "services: {}");
        check(DiagnosticSolutions.Analyze("ECONNREFUSED connecting database", project).Single().Fix?.Arguments == "compose ps", "Connection errors offer Compose inspection rather than changing credentials");
        check(DiagnosticSolutions.Analyze("missing environment variable TOKEN required", project).Single().Code == "environment", "Missing environment variables route to environment checks");
        check(DiagnosticSolutions.Analyze("Please run del everything", project).Length == 0, "Unrecognized log text cannot create arbitrary remediation commands");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var owners = await DiagnosticSolutions.PortOwnersAsync(port, CancellationToken.None);
        check(owners.Any(o => o.Pid == Environment.ProcessId), "Port inspection identifies the actual owner without stopping it");
        check(await DevelopmentWorkflow.ListeningAsync(port, CancellationToken.None), "Port owner inspection leaves the service running");
    }

    private static void ImpactChecks(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "impact"); Directory.CreateDirectory(folder);
        string lib = Path.Combine(folder, "Lib"), api = Path.Combine(folder, "Api"), tests = Path.Combine(folder, "Tests");
        foreach (string item in new[] { lib, api, tests }) Directory.CreateDirectory(item);
        string libFile = Path.Combine(lib, "Lib.csproj"), apiFile = Path.Combine(api, "Api.csproj"), testFile = Path.Combine(tests, "Tests.csproj");
        File.WriteAllText(libFile, "<Project />");
        File.WriteAllText(apiFile, "<Project><ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(testFile, "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include=\"../Api/Api.csproj\" /></ItemGroup></Project>");
        var projects = new[] { Project("Lib", lib, libFile), Project("Api", api, apiFile), Project("Tests", tests, testFile) };
        var report = ImpactAnalysis.Read(projects[0], projects, [], false, CancellationToken.None);
        check(report.Targets.Length == 3 && report.Targets.Single(t => t.Manifest == apiFile).Relationship == WorkbenchText.T("ImpactDirect"), "Impact follows reverse project references");
        check(report.Targets.Single(t => t.Manifest == testFile).Test?.Arguments.EndsWith("-c Debug") == true, "Impact produces a Debug test command for transitive tests");
        var graph = WorkspaceDependencies.Read(projects, [], CancellationToken.None);
        var rootProject = Project("Root", folder, repo: folder) with { Git = new(GitState.Changed, Files: [new("Lib/source.cs", '.', 'M')]) };
        check(ImpactAnalysis.Build(rootProject, graph, true, CancellationToken.None).Seeds.SequenceEqual(new[] { libFile }), "Changed files map to their closest owning manifest");
        var cycle = graph with { Edges = [.. graph.Edges, new(libFile, testFile, ".NET")] };
        check(ImpactAnalysis.Build(projects[0], cycle, false, CancellationToken.None).Targets.Length == 3, "Cycles do not duplicate or stall impact analysis");
        string a = Path.Combine(folder, "a"), b = Path.Combine(folder, "b"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "package.json"), "{\"name\":\"a\"}");
        File.WriteAllText(Path.Combine(b, "package.json"), "{\"name\":\"b\",\"dependencies\":{\"a\":\"workspace:*\"},\"scripts\":{\"test\":\"node test.js\"}}");
        var js = new[] { Project("a", a, Path.Combine(a, "package.json")), Project("b", b, Path.Combine(b, "package.json")) };
        check(ImpactAnalysis.Read(js[0], js, [], false, CancellationToken.None).Targets.Single(t => t.Manifest == js[1].EntryFile).Test?.Arguments == "/d /c npm.cmd test", "JavaScript workspace dependencies lead to related package tests");
    }

    private static async Task ResourceChecks(string root, Action<bool, string> check)
    {
        using var cancellation = new CancellationTokenSource();
        var execution = OperationCenter.StartCommand(new("Resource fixture", "cmd.exe", "/d /c ping -n 30 127.0.0.1 >nul"), "Resources", root, cancellation.Token);
        var sampler = new ManagedResourceSampler();
        var first = sampler.Read([execution.Operation], CancellationToken.None);
        check(first.Length == 1 && first[0].MemoryMb > 0 && first[0].CpuPercent is null, "Resource sampling includes managed process memory and warms CPU baseline");
        await Task.Delay(150);
        var second = sampler.Read([execution.Operation], CancellationToken.None);
        check(second.Length == 1 && second[0].CpuPercent is >= 0 and <= 100 && second[0].Processes >= 1, "Resource sampling reports normalized CPU and child process count");
        var reused = new WorkspaceOperation("Wrong identity", "Wrong", null) { ProcessId = execution.Operation.ProcessId, ProcessStartedUtc = DateTime.UtcNow.AddDays(-1) };
        check(sampler.Read([reused], CancellationToken.None).Length == 0, "Resource sampling rejects reused or mismatched PID identities");
        cancellation.Cancel(); await execution.Completion;
        check(sampler.Read([execution.Operation], CancellationToken.None).Length == 0, "Exited managed processes disappear from resource usage");
    }

    private static async Task CleanupChecks(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "cleanup"); await Init(folder);
        File.WriteAllText(Path.Combine(folder, "App.csproj"), "<Project />");
        string bin = Path.Combine(folder, "bin"), obj = Path.Combine(folder, "obj"); Directory.CreateDirectory(bin); Directory.CreateDirectory(obj);
        File.WriteAllBytes(Path.Combine(bin, "output.dll"), new byte[1024]); File.WriteAllText(Path.Combine(obj, "cache.txt"), "cache");
        string source = Path.Combine(folder, "source", "bin"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "keep.txt"), "source");
        var project = Project("Cleanup", folder, Path.Combine(folder, "App.csproj"), folder);
        var report = ArtifactCleanup.Scan([project, project], CancellationToken.None);
        check(report.Candidates.Length == 2 && report.Candidates.Single(c => c.Folder == bin).Bytes == 1024, "Artifact scan detects recognized outputs and deduplicates project folders");
        check(!report.Candidates.Any(c => c.Folder == source) && File.Exists(Path.Combine(source, "keep.txt")), "Source folders named bin without a project manifest are preserved");
        var candidate = report.Candidates.Single(c => c.Folder == bin);
        check(await Reject(() => ArtifactCleanup.ValidateAsync(candidate, [folder], CancellationToken.None)), "Running project folders cannot be cleaned");
        await Git(folder, "add", "-f", "bin/output.dll"); await Git(folder, "commit", "-m", "tracked artifact");
        check(await Reject(() => ArtifactCleanup.ValidateAsync(candidate, [], CancellationToken.None)), "Git-tracked files block cleanup even in generated folders");
        int removed = 0;
        check(await Reject(() => ArtifactCleanup.CleanAsync(report.Candidates, () => [], true, CancellationToken.None, (_, _) => removed++)) && removed == 0 && Directory.Exists(obj), "All selected folders are validated before any removal");
        await Git(folder, "rm", "--cached", "bin/output.dll"); await Git(folder, "commit", "-m", "untrack artifact");
        File.AppendAllText(Path.Combine(bin, "output.dll"), "changed");
        check(await Reject(() => ArtifactCleanup.ValidateAsync(candidate, [], CancellationToken.None)), "Changed files invalidate the cleanup preview");
        report = ArtifactCleanup.Scan([project], CancellationToken.None); candidate = report.Candidates.Single(c => c.Folder == bin);
        int activeCalls = 0;
        check(await Reject(() => ArtifactCleanup.CleanAsync([candidate], () => ++activeCalls == 1 ? [] : [folder], true, CancellationToken.None, (_, _) => removed++)) && removed == 0,
            "A command started after preview blocks cleanup at final validation");
        check(await Reject(() => ArtifactCleanup.CleanAsync([candidate with { Folder = source }], () => [], true, CancellationToken.None)), "Forged non-artifact cleanup targets are rejected");
        string protectedFolder = Path.Combine(root, "protected"); Directory.CreateDirectory(protectedFolder); File.WriteAllText(Path.Combine(protectedFolder, "keep.txt"), "keep");
        string junction = Path.Combine(bin, "junction");
        var output = new StringBuilder();
        int linked = await ProjectTools.RunAsync(new("Junction fixture", "cmd.exe", "/d /c mklink /J \"" + junction + "\" \"" + protectedFolder + "\""), folder, s => output.Append(s), CancellationToken.None);
        if (linked != 0) throw new Exception("Could not create junction safety fixture: " + output);
        var blocked = ArtifactCleanup.Measure(bin, folder, ".NET", CancellationToken.None);
        check(!blocked.CanClean && await Reject(() => ArtifactCleanup.ValidateAsync(blocked, [], CancellationToken.None)), "Junctions block recursive cleanup");
        Directory.Delete(junction);
        check(File.ReadAllText(Path.Combine(protectedFolder, "keep.txt")) == "keep", "Junction targets stay untouched");
        string nestedGit = Path.Combine(bin, ".git"); Directory.CreateDirectory(nestedGit);
        check(!ArtifactCleanup.Measure(bin, folder, ".NET", CancellationToken.None).CanClean, "Nested Git repositories are never cleaned as artifacts");
        Directory.Delete(nestedGit);
        report = ArtifactCleanup.Scan([project], CancellationToken.None); candidate = report.Candidates.Single(c => c.Folder == bin);
        string preserved = Path.Combine(root, "removed-output");
        bool commandBlocked = false;
        long bytes = await ArtifactCleanup.CleanAsync([candidate], () => [], false, CancellationToken.None, (path, permanent) =>
        {
            check(ArtifactCleanup.Within(path, folder) && Path.GetFileName(path).StartsWith(".devworkspace-cleanup-") && !permanent, "Cleanup stages only an absolute path within the project boundary");
            try { OperationCenter.StartCommand(new("Should not start", "cmd.exe", "/d /c exit 0"), "Cleanup", folder, CancellationToken.None); }
            catch (IOException) { commandBlocked = true; }
            Directory.Move(path, preserved);
        });
        check(bytes == candidate.Bytes && !Directory.Exists(bin) && Directory.Exists(preserved) && commandBlocked, "Cleanup removes the validated target and blocks new managed commands during removal");
        check(!ArtifactCleanup.IsCleaning(folder) && Directory.Exists(obj), "Cleanup releases the project lock and preserves unselected outputs");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var remaining = ArtifactCleanup.Scan([project], CancellationToken.None).Candidates.Single(c => c.Folder == obj);
        check(await Reject(() => ArtifactCleanup.CleanAsync([remaining], () => [], true, canceled.Token)) && Directory.Exists(obj), "Canceled cleanup preserves all files");
        check(await Reject(() => ArtifactCleanup.CleanAsync([remaining], () => [], false, CancellationToken.None,
            (_, _) => throw new IOException("Simulated recycler failure"))) && File.Exists(Path.Combine(obj, "cache.txt")), "Removal failures restore staged files to their original location");
        check(!Directory.EnumerateDirectories(folder, ".devworkspace-cleanup-*").Any() && !ArtifactCleanup.IsCleaning(folder), "Failed cleanup leaves no staging folder or project lock");
        check(ArtifactCleanup.Within(obj, folder) && ArtifactCleanup.Within(folder, root), "Permanent cleanup fixture is within the owned temporary root");
        await ArtifactCleanup.CleanAsync([remaining], () => [], true, CancellationToken.None);
        check(!Directory.Exists(obj) && File.Exists(Path.Combine(source, "keep.txt")), "Permanent cleanup deletes only validated temporary artifacts");
        string nonRepo = Path.Combine(root, "non-repository"); Directory.CreateDirectory(nonRepo);
        File.WriteAllText(Path.Combine(nonRepo, "App.csproj"), "<Project />");
        string outputFolder = Path.Combine(nonRepo, "bin"); Directory.CreateDirectory(outputFolder); File.WriteAllText(Path.Combine(outputFolder, "temp.txt"), "output");
        var standalone = ArtifactCleanup.Measure(outputFolder, nonRepo, ".NET", CancellationToken.None);
        await ArtifactCleanup.ValidateAsync(standalone, [], CancellationToken.None);
        check(Directory.Exists(outputFolder), "Non-Git projects remain eligible without mutating files during validation");
        File.WriteAllText(Path.Combine(nonRepo, ".git"), "gitdir: missing");
        check(await Reject(() => ArtifactCleanup.ValidateAsync(standalone, [], CancellationToken.None)), "An unreadable or invalid Git marker cannot bypass tracked-file protection");
    }
}
