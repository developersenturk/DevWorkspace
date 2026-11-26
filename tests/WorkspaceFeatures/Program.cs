using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DevWorkspace;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; }
    private static ProjectEntry Project(string name, string folder, string? entry = null, string? repository = null) => new(name, "", name, folder, entry, "", repository, new(GitState.None));
    private static async Task Git(string folder, params string[] args)
    {
        var (code, _, error) = await GitProjectOperations.RunAsync(folder, args, CancellationToken.None);
        if (code != 0) throw new Exception(error);
    }
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--diff-navigation"])
        {
            var application = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            var owner = new MainWindow();
            var project = Project("Diff test", Environment.CurrentDirectory, repository: Environment.CurrentDirectory);
            var view = new GitWorkbenchView(owner, project);
            typeof(MainWindow).GetField("_gitView", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(owner, view);
            var working = (DevExpress.Xpf.Editors.ListBoxEdit)view.FindName("WorkingFiles");
            var staged = (DevExpress.Xpf.Editors.ListBoxEdit)view.FindName("StagedFiles");
            var previous = (System.Windows.Controls.Button)owner.FindName("PreviousGitDiffButton");
            var next = (System.Windows.Controls.Button)owner.FindName("NextGitDiffButton");
            owner.UpdateGitDiffNavigation();
            Check(!previous.IsEnabled && !next.IsEnabled, "Empty list disables navigation");
            GitFileChange shared = new("shared.txt", 'M', 'M');
            working.ItemsSource = new GitFileChange[] { new("new.txt", '?', '?'), shared };
            staged.ItemsSource = new GitFileChange[] { shared };
            view.NavigateDiff(1);
            Check(view.DiffPosition == (0, 3) && working.SelectedItem is GitFileChange { Path: "new.txt" }, "Starts at first working file");
            Check(!previous.IsEnabled && next.IsEnabled, "First file boundary");
            view.NavigateDiff(-1);
            Check(view.DiffPosition.Index == 0, "Previous does not wrap");
            view.NavigateDiff(1);
            Check(view.DiffPosition.Index == 1 && ReferenceEquals(working.SelectedItem, shared), "Next working file");
            view.NavigateDiff(1);
            Check(view.DiffPosition.Index == 2 && ReferenceEquals(staged.SelectedItem, shared) && working.SelectedItems.Count == 0, "Same path has separate staged diff");
            Check(previous.IsEnabled && !next.IsEnabled, "Last file boundary");
            view.NavigateDiff(1);
            Check(view.DiffPosition.Index == 2, "Next does not wrap");
            view.NavigateDiff(-1);
            Check(view.DiffPosition.Index == 1 && staged.SelectedItem is null, "Back across staged boundary");
            working.UnSelectAll();
            view.NavigateDiff(-1);
            Check(view.DiffPosition.Index == 2, "Previous without selection starts at last diff");
            owner.Close();
            application.Shutdown();
            Console.WriteLine($"Diff navigation: {_checks} checks passed.");
            return;
        }
        if (args is ["--startup-preview", var startupTheme, var startupLanguage])
        {
            var application = new App();
            Loc.Initialize(startupLanguage);
            application.Resources.MergedDictionaries.Add(Loc.CreateResources());
            App.ApplyTheme(startupTheme == "light" ? DevExpress.Xpf.Core.LightweightTheme.Win11Light : DevExpress.Xpf.Core.LightweightTheme.Win11Dark);
            var window = new StartupWindow(application.Resources);
            application.MainWindow = window; application.Run(window); return;
        }
        if (args is ["--grid-preview", var kind, var themeName])
        {
            var application = new App();
            Loc.Initialize("tr");
            App.ApplyTheme(themeName == "light" ? DevExpress.Xpf.Core.LightweightTheme.Win11Light : DevExpress.Xpf.Core.LightweightTheme.Win11Dark);
            var project = Project("DevWorkspace", Environment.CurrentDirectory, repository: Environment.CurrentDirectory);
            System.Windows.Window window = kind switch
            {
                "fetch" => new BatchFetchWindow([project], CancellationToken.None),
                "inspection" => new ProjectInspectionWindow("Proje inceleme", _ => Task.FromResult(new[] { new InspectionRow("Tamam", "Örnek", "Tema ve satır görünümü") })),
                "findings" => new TrackedFindingsWindow([new("preview", "DevWorkspace", "Örnek bulgu", "Tema ve seçim kontrolü", "", DateTimeOffset.Now)], _ => { }),
                "worktrees" => new GitWorktreeWindow(Environment.CurrentDirectory, _ => true),
                _ => new OperationCenterWindow()
            };
            window.ShowInTaskbar = true;
            application.MainWindow = window;
            application.Run(window);
            return;
        }
        if (args is ["--ui-smoke"])
        {
            var application = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            var workspace = new Workspace(Path.GetTempPath(), new(1, "UI smoke", [], []));
            var project = Project("Example", workspace.Root);
            foreach (string language in new[] { "tr", "en" })
            foreach (var theme in new[] { DevExpress.Xpf.Core.LightweightTheme.Win11Light, DevExpress.Xpf.Core.LightweightTheme.Win11Dark })
            {
                Loc.Initialize(language); App.ApplyTheme(theme);
                var tools = new WorkspaceToolsWindow(workspace, [project], 0, () => [], _ => true, CancellationToken.None, _ => { }, _ => { });
                tools.Show();
                for (int tab = 0; tab < 6; tab++) { tools.SelectTab(tab); tools.UpdateLayout(); Check(tools.ActualWidth >= tools.MinWidth, $"Tools layout {language}/{theme.Name}/{tab}"); }
                GridViewChecks.Run(tools, project, Check);
                tools.Close();
                var note = new ResumeNoteWindow("Example", new("Context", "Next", DateTime.UtcNow), _ => true); note.Show(); note.UpdateLayout(); Check(note.ActualWidth >= note.MinWidth, "Note layout"); note.Close();
                var profiles = new LaunchProfilesWindow([project], [], _ => { }, workspace.Root); profiles.Show(); profiles.UpdateLayout(); Check(profiles.ActualWidth >= profiles.MinWidth, "Profile layout"); profiles.Close();
                var hub = new DevelopmentHubWindow(workspace, () => [project], () => new(null, []), (_, _) => true,
                    _ => Task.CompletedTask, () => { }, () => { }, _ => { }, _ => Task.CompletedTask, _ => { }, _ => { }, () => { });
                hub.Show(); hub.UpdateLayout(); Check(hub.ActualWidth >= hub.MinWidth, "Development hub layout");
                hub.Width = hub.MinWidth; hub.Height = hub.MinHeight; hub.UpdateLayout();
                Check(hub.ActualWidth == hub.MinWidth, "Development hub minimum width"); hub.Close();
                var productivity = new ProductivityWindow(workspace, () => [project], () => new(null, []), _ => true,
                    (_, _, _) => Task.CompletedTask, _ => Task.CompletedTask, _ => { }, _ => { }, _ => { }, CancellationToken.None);
                productivity.Show();
                for (int tab = 0; tab < 5; tab++) { productivity.SelectTab(tab); productivity.UpdateLayout(); Check(productivity.ActualWidth >= productivity.MinWidth, $"Productivity tab {language}/{theme.Name}/{tab}"); }
                productivity.Width = productivity.MinWidth; productivity.Height = productivity.MinHeight; productivity.UpdateLayout();
                Check(productivity.ActualWidth == productivity.MinWidth, "Productivity minimum width"); productivity.Close();
            }
            application.Shutdown(); Console.WriteLine($"Workspace UI: {_checks} theme/language/layout checks passed."); return;
        }
        if (args is ["--preview", var previewFolder])
        {
            var application = new App();
            var workspace = new Workspace(Path.GetFullPath(previewFolder), new(1, "Preview", [], []));
            var previewProject = Project("DevWorkspace", workspace.Root, Path.Combine(workspace.Root, "src", "DevWorkspace.csproj"), workspace.Root);
            var window = new WorkspaceToolsWindow(workspace, [previewProject], 0, () => [], _ => true, CancellationToken.None, _ => { }, _ => { });
            window.ShowInTaskbar = true;
            application.MainWindow = window; application.Run(window); return;
        }
        RunAsync(args).GetAwaiter().GetResult();
    }
    private static async Task RunAsync(string[] args)
    {
        if (args is ["--echo-env", var variable]) { Console.WriteLine(Environment.GetEnvironmentVariable(variable)); return; }
        if (args is ["--listener", var requestedPort])
        {
            var listener = new TcpListener(IPAddress.Loopback, int.Parse(requestedPort)); listener.Start();
            Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port); await Task.Delay(Timeout.Infinite); return;
        }
        if (args is ["--listener"])
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
            await Task.Delay(Timeout.Infinite); return;
        }
        Loc.Initialize("tr");
        string root = Path.Combine(Path.GetTempPath(), "DevWorkspace-features-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await DevelopmentWorkflowChecks.RunAsync(root, Check);
            await ProductivityChecks.RunAsync(root, Check);
            string folder = Path.Combine(root, "search"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "source.cs"), "first\nNeedle here\nneedle again\n");
            Directory.CreateDirectory(Path.Combine(folder, "node_modules")); File.WriteAllText(Path.Combine(folder, "node_modules", "hidden.js"), "Needle");
            File.WriteAllText(Path.Combine(folder, "binary.cs"), "\0Needle");
            File.WriteAllText(Path.Combine(folder, ".env"), "TOKEN=Needle");
            var project = Project("Search", folder);
            var search = WorkspaceSearch.Find([project, project], [], "Needle", false, CancellationToken.None);
            Check(search.Hits.Length == 2 && search.Hits[0].Line == 2 && search.Hits.All(h => h.File.EndsWith("source.cs")), "Search exclusions, binary detection, line numbers and overlapping projects");
            Check(WorkspaceSearch.Find([project], [], "Needle", true, CancellationToken.None).Hits.Length == 1, "Case-sensitive search");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool didCancel = false; try { WorkspaceSearch.Find([project], [], "x", false, cancelled.Token); } catch (OperationCanceledException) { didCancel = true; }
            Check(didCancel, "Search cancellation");

            foreach (var (installed, required, expected) in new (string, string, bool?)[] {
                ("v22.11.0", ">=20 <23", true), ("v18.2.0", ">=20", false), ("v22.11.0", "^22.0.0", true),
                ("v23.0.0", "^22.0.0", false), ("v20.9.0", "<=20", true), ("v20.9.0", ">20", false),
                ("v0.0.4", "^0.0.3", false), ("v0.8.0", "^0", true), ("v22.11.0", "lts/*", null),
                ("v22.11.0", "22.x", true), ("v22.11.0", "~22.10.0", false), ("v22.11.0", "18 || >=22", true) })
                Check(EnvironmentChecks.MatchesNode(installed, required) == expected, "Node range: " + required);
            Check(EnvironmentChecks.EnvKeys(["# A=ignored", "export HOST=localhost", "TOKEN=", "INVALID-X=x"]).SetEquals(["HOST", "TOKEN"]), "Only env key names parsed");
            File.WriteAllText(Path.Combine(folder, ".env.example"), "TOKEN=\nHOST=\n");
            var environment = await EnvironmentChecks.ReadAsync(project, CancellationToken.None);
            Check(environment.Any(r => r.Name == ".env" && r.Detail.Contains("HOST") && !r.Detail.Contains("Needle")), "Environment reports missing keys without exposing values");

            string a = Path.Combine(root, "A.csproj"), b = Path.Combine(root, "B.csproj"), c = Path.Combine(root, "C.csproj");
            File.WriteAllText(a, "<Project><ItemGroup><ProjectReference Include=\"B.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(b, "<Project><ItemGroup><ProjectReference Include=\"C.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(c, "<Project><ItemGroup><ProjectReference Include=\"A.csproj\" /></ItemGroup></Project>");
            var graph = WorkspaceDependencies.Read([Project("Graph", root, a)], [], CancellationToken.None);
            Check(graph.Reachable(a, false).ToHashSet().SetEquals([b, c]), "Transitive dependencies terminate on cycles");
            Check(graph.Reachable(c, true).ToHashSet().SetEquals([a, b]), "Reverse dependency impact");
            string js = Path.Combine(root, "js"); Directory.CreateDirectory(js); Directory.CreateDirectory(Path.Combine(js, "shared"));
            File.WriteAllText(Path.Combine(js, "package.json"), "{\"name\":\"app\",\"dependencies\":{\"shared\":\"file:./shared\"}}");
            File.WriteAllText(Path.Combine(js, "shared", "package.json"), "{\"name\":\"shared\"}");
            graph = WorkspaceDependencies.Read([Project("JS", js)], [], CancellationToken.None);
            Check(graph.Edges.Length == 1 && graph.Edges[0].Target == Path.Combine(js, "shared", "package.json"), "Local JS dependency");
            var services = WorkspaceDependencies.ParseCompose("compose.yaml", "{\"services\":{\"api\":{\"depends_on\":{\"db\":{\"condition\":\"service_healthy\"}}},\"web\":{\"depends_on\":[\"api\"]}}}");
            Check(services.Length == 2 && new DependencyReport(services, []).Reachable("compose.yaml#db", true).Length == 2, "Compose service graph includes transitive impact");

            string template = Path.Combine(root, "template"); Directory.CreateDirectory(template);
            File.WriteAllText(Path.Combine(template, "__PROJECT_NAME__.txt"), "Hello {{ProjectName}}");
            File.WriteAllText(Path.Combine(template, ".env"), "SECRET=hidden"); File.WriteAllText(Path.Combine(template, ".env.example"), "SECRET=");
            var plan = ProjectTemplates.Preview(new("Custom", template), "Sample", CancellationToken.None);
            Check(plan.Any(f => f.RelativePath == "Sample.txt" && Encoding.UTF8.GetString(f.Content) == "Hello Sample"), "Template substitution");
            Check(!plan.Any(f => f.RelativePath == ".env") && plan.Any(f => f.RelativePath == ".env.example"), "Template excludes local secrets");
            string created = await ProjectTemplates.CreateAsync(root, "Sample", plan, false, CancellationToken.None);
            Check(File.ReadAllText(Path.Combine(created, "Sample.txt")) == "Hello Sample", "Create reviewed template");
            bool rejected = false; try { await ProjectTemplates.CreateAsync(root, "Sample", plan, false, CancellationToken.None); } catch (IOException) { rejected = true; }
            Check(rejected, "Existing project never overwritten");
            rejected = false; try { await ProjectTemplates.CreateAsync(root, "Escape", [new("../escape.txt", [1])], false, CancellationToken.None); } catch (IOException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(root, "escape.txt")) && !Directory.Exists(Path.Combine(root, "Escape")), "Template traversal rejected and staging removed");
            foreach (string invalid in new[] { "../Bad", "CON", "with space", "x:evil", "" })
            { rejected = false; try { ProjectTemplates.ValidateName(invalid); } catch (IOException) { rejected = true; } Check(rejected, "Invalid project name: " + invalid); }
            foreach (var builtin in ProjectTemplates.BuiltIns)
            {
                var generated = ProjectTemplates.Preview(builtin, "Generated", CancellationToken.None);
                Check(generated.Length >= 3, "Built-in template: " + builtin.Name);
                string destination = Path.Combine(root, builtin.Kind); Directory.CreateDirectory(destination);
                string generatedFolder = await ProjectTemplates.CreateAsync(destination, "Generated", generated, false, CancellationToken.None);
                var buildCommand = builtin.Kind == "node" ? new ProjectCommand("Check", "node.exe", "--check index.js") : new ProjectCommand("Build", "dotnet.exe", "build -c Debug");
                var buildOutput = new StringBuilder();
                int code = await ProjectTools.RunAsync(buildCommand, generatedFolder, text => { lock (buildOutput) buildOutput.Append(text); }, CancellationToken.None);
                Check(code == 0, "Generated template compiles: " + builtin.Name + " " + buildOutput);
            }

            var session = new SessionState(); session.Workspaces[root] = new(null, [], Profiles: [new("Start", [new("Search", null, "terminal")])], ResumeNotes: new() { ["Search"] = new("Context", "Next", DateTime.UtcNow) });
            string sessionPath = Path.Combine(root, "session.json"); Check(session.SaveTo(sessionPath), "Save notes/profiles");
            var loaded = SessionState.LoadFrom(sessionPath); Check(loaded.For(root).ResumeNotes!["Search"].Next == "Next" && loaded.For(root).Profiles![0].Steps[0].Action == "terminal", "Notes and profile steps persist");
            File.WriteAllText(sessionPath, "{\"Workspaces\":{\"old\":{\"RecentProjects\":[],\"Profiles\":[{\"Name\":\"Legacy\",\"Steps\":[{\"Project\":\"x\"}]}]}}}");
            Check(SessionState.LoadFrom(sessionPath).For("old").Profiles![0].Steps[0].Action == "default", "Legacy profiles remain compatible");

            string repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo); await Git(repo, "init");
            await Git(repo, "config", "user.name", "Feature Tests"); await Git(repo, "config", "user.email", "tests@example.invalid");
            File.WriteAllText(Path.Combine(repo, "file.txt"), "one"); await Git(repo, "add", "."); await Git(repo, "commit", "-m", "initial");
            await Git(repo, "branch", "upstream"); await Git(repo, "branch", "--set-upstream-to=upstream");
            File.WriteAllText(Path.Combine(repo, "file.txt"), "two"); await Git(repo, "commit", "-am", "second");
            File.WriteAllText(Path.Combine(repo, "file.txt"), "three"); await Git(repo, "stash", "push");
            var summaries = await WorkspaceGitSummary.ReadAsync([Project("Repo", repo, repository: repo), Project("Duplicate", repo, repository: repo)], CancellationToken.None);
            Check(summaries.Length == 1 && summaries[0].Ahead == 1 && summaries[0].Behind == 0 && summaries[0].Stashes == 1, "Git overview uses fresh tracking data, stashes and distinct repos");

            using var run = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = new StringBuilder(); var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
            var command = new ProjectCommand("Listener", Environment.ProcessPath!, "--listener");
            var running = ProjectTools.RunAsync(command, root, text => { lock (output) output.Append(text); }, run.Token, p => started.SetResult(p));
            var process = await started.Task; int pid = process.Id;
            var operation = new WorkspaceOperation("Listener", "Test", null) { Command = command, Folder = root, ProcessId = pid, ProcessStartedUtc = process.StartTime.ToUniversalTime() };
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await RuntimePorts.RefreshAsync([operation], run.Token);
                if (operation.Ports != "—") break;
                await Task.Delay(100);
            }
            Check(operation.Ports.StartsWith("127.0.0.1:"), "Runtime discovers managed listening port");
            run.Cancel(); try { await running; } catch (OperationCanceledException) { }
            bool exited; try { using var p = Process.GetProcessById(pid); exited = p.HasExited; } catch (ArgumentException) { exited = true; }
            Check(exited, "Stopping command terminates managed process");
            foreach (string key in WorkbenchText.Messages.Keys)
            {
                Loc.Initialize("tr"); var tr = System.Text.CompositeFormat.Parse(WorkbenchText.T(key));
                Loc.Initialize("en"); var en = System.Text.CompositeFormat.Parse(WorkbenchText.T(key));
                Check(tr.MinimumArgumentCount == en.MinimumArgumentCount, "Translation placeholders: " + key);
            }
            Console.WriteLine($"Workspace features: {_checks} checks passed.");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("DevWorkspace-features-", StringComparison.Ordinal))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }
}
