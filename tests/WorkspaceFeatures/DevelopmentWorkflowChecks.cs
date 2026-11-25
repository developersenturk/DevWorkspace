using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DevWorkspace;

internal static class DevelopmentWorkflowChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "workflow"); Directory.CreateDirectory(folder);
        var project = new ProjectEntry("Workflow", "", "workflow", folder, null, "", null, new(GitState.None));
        var profile = new LaunchProfile("Development", [new("workflow", new("Build", "dotnet.exe", "build -c Debug"), WaitForExit: true, TimeoutSeconds: 30)]);
        string json = DevelopmentConfigurationFile.Export([profile], root, ["dotnet.exe"]);
        var imported = DevelopmentConfigurationFile.Import(json, Path.Combine(root, "other"));
        check(imported.Profiles[0].Steps[0].WaitForExit && imported.RequiredTools.SequenceEqual(new[] { "dotnet.exe" }), "Team configuration preserves completion and tool requirements");
        var rooted = profile with { Steps = [new("workflow", new("Build", "dotnet.exe", "build \"" + Path.Combine(root, "app.csproj") + "\""))] };
        string portable = DevelopmentConfigurationFile.Export([rooted], root);
        check(portable.Contains("${workspace}") && !portable.Contains(JsonSerializer.Serialize(root)[1..^1]), "Workspace root is portable");
        check(DevelopmentConfigurationFile.Import(portable, folder).Profiles[0].Steps[0].Command!.Arguments.Contains(folder), "Workspace variables resolve on another machine");
        foreach (string invalid in new[] {
            "{\"schemaVersion\":2,\"profiles\":[],\"requiredTools\":[]}",
            json.Replace("workflow", "../outside"),
            json.Replace("dotnet.exe", "C:/machine/dotnet.exe"),
            json.Replace("\"timeoutSeconds\": 30", "\"timeoutSeconds\": 0"),
            json.Replace("\"arguments\": \"build -c Debug\"", "\"arguments\": null"),
            json.Replace("\"requiredTools\": [", "\"requiredTools\": [null,"),
            "{\"schemaVersion\":1,\"profiles\":[null],\"requiredTools\":[]}" })
        {
            bool rejected = false; try { DevelopmentConfigurationFile.Import(invalid, root); } catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { rejected = true; }
            check(rejected, "Invalid team configuration rejected");
        }
        string sample = Path.Combine(folder, ".env.example"); File.WriteAllText(sample, "API_URL=http://localhost:5100\nTOKEN=\n");
        EnvironmentChecks.CreateEnvTemplate(folder);
        check(File.ReadAllText(Path.Combine(folder, ".env")) == File.ReadAllText(sample), ".env is created from project template");
        File.WriteAllText(Path.Combine(folder, ".env"), "KEEP=local");
        bool refused = false; try { EnvironmentChecks.CreateEnvTemplate(folder); } catch (IOException) { refused = true; }
        check(refused && File.ReadAllText(Path.Combine(folder, ".env")) == "KEEP=local", "Existing .env is preserved");
        File.Delete(Path.Combine(folder, ".env")); File.Delete(sample);

        string file = Path.Combine(folder, "file with spaces.cs"); File.WriteAllText(file, "// test");
        check(LogLocation.Parse(file + "(12,3): error CS0000", folder) is { Line: 12, Column: 3 }, "MSBuild file locations with spaces");
        check(LogLocation.Parse("at handler (" + file + ":8:2)", folder) is { Line: 8, Column: 2 }, "Stack locations open the exact line");
        check(LogLocation.Parse("nonexistent.cs:1:2", folder) is null, "Missing log files do not produce editor links");
        check(DevelopmentPreflight.Blocks(new("test", WorkbenchText.T("Attention"), ".env", "missing")), "Missing environment keys block startup");
        check(!DevelopmentPreflight.Blocks(new("test", WorkbenchText.T("Attention"), "Docker Compose", "stopped")), "Stopped Docker containers can be started by the profile");
        const string compose = "{\"State\":\"running\",\"Publishers\":[{\"PublishedPort\":5100,\"Protocol\":\"tcp\"}]}";
        check(DevelopmentRun.ComposeOwnsPort(compose, 5100), "Docker readiness checks the project's published TCP port");
        check(!DevelopmentRun.ComposeOwnsPort(compose, 5200) && !DevelopmentRun.ComposeOwnsPort(compose.Replace("running", "exited"), 5100), "Unrelated ports and stopped Docker services are not ready");
        check(!DevelopmentRun.ComposeOwnsPort(compose.Replace("tcp", "udp"), 5100), "UDP publishing does not satisfy TCP readiness");

        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        check(await DevelopmentWorkflow.ListeningAsync(port, CancellationToken.None), "Readiness detects a listening service");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool canceledWait = false;
            try { await DevelopmentWorkflow.AwaitReadyAsync(new("workflow", new("test", "cmd.exe"), WaitForExit: true), Task.Delay(5000), () => false, _ => { }, canceled.Token); }
            catch (OperationCanceledException) { canceledWait = true; }
            check(canceledWait, "Cancel interrupts readiness wait");
        }
        listener.Stop();
        bool timedOut = false;
        try { await DevelopmentWorkflow.AwaitReadyAsync(new("workflow", new("test", "cmd.exe"), ReadyPort: port, TimeoutSeconds: 1), Task.Delay(5000), () => false, _ => { }, CancellationToken.None); }
        catch (IOException) { timedOut = true; }
        check(timedOut, "Unavailable service readiness times out");

        string marker = Path.Combine(folder, "order.txt");
        using (var run = new DevelopmentRun(CancellationToken.None))
        {
            await run.StartAsync(new("Ordered", [new("workflow", new("First", "cmd.exe", "/d /c echo first>order.txt"), WaitForExit: true),
                new("workflow", new("Second", "cmd.exe", "/d /c echo second>>order.txt"), WaitForExit: true)]), [project], _ => Task.CompletedTask, _ => { }, _ => { });
            check(File.ReadAllLines(marker).Select(l => l.Trim()).SequenceEqual(new[] { "first", "second" }), "Profile tasks execute in order");
            check(!run.IsActive, "Completed profile can run again");
        }
        File.Delete(marker);
        using (var run = new DevelopmentRun(CancellationToken.None))
        {
            var service = new ProjectCommand("Managed listener", Environment.ProcessPath!, "--listener " + port);
            await run.StartAsync(new("Service then task", [new("workflow", service, ReadyPort: port, TimeoutSeconds: 15),
                new("workflow", new("Dependent", "cmd.exe", "/d /c echo ready>order.txt"), WaitForExit: true)]), [project], _ => Task.CompletedTask, _ => { }, _ => { });
            var operation = OperationCenter.Entries.First(o => o.Command == service);
            check(File.Exists(marker) && operation.Ports.EndsWith(":" + port), "Dependent task waits for a port owned by the launched process");
            run.Stop();
            for (int attempt = 0; operation.IsRunning && attempt < 60; attempt++) await Task.Delay(50);
            check(!operation.IsRunning, "Stopping a profile terminates its ready service");
        }
        File.Delete(marker);
        using (var run = new DevelopmentRun(CancellationToken.None))
        {
            bool failed = false;
            try { await run.StartAsync(new("Fail", [new("workflow", new("Fail", "cmd.exe", "/d /c exit 7"), WaitForExit: true),
                new("workflow", new("Never", "cmd.exe", "/d /c echo unexpected>order.txt"), WaitForExit: true)]), [project], _ => Task.CompletedTask, _ => { }, _ => { }); }
            catch (IOException) { failed = true; }
            check(failed && !File.Exists(marker), "Failed task prevents dependent tasks from starting");
        }
        using (var run = new DevelopmentRun(CancellationToken.None))
        {
            // The child waits long enough to test process-tree termination without a shell daemon.
            var command = new ProjectCommand("Long task", "cmd.exe", "/d /c ping -n 30 127.0.0.1 >nul");
            await run.StartAsync(new("Managed", [new("workflow", command)]), [project], _ => Task.CompletedTask, _ => { }, _ => { });
            var operation = OperationCenter.Entries.First(o => o.Command == command);
            run.Stop();
            for (int attempt = 0; operation.IsRunning && attempt < 60; attempt++) await Task.Delay(50);
            check(!operation.IsRunning, "Stop terminates managed profile command");
            bool exited; try { using var process = Process.GetProcessById(operation.ProcessId!.Value); exited = process.HasExited; } catch (ArgumentException) { exited = true; }
            check(exited, "Profile process has exited after stop");
        }
        var session = new SessionState();
        session.Workspaces[root] = new(null, [], Profiles: [profile], LastProfile: profile.Name, RequiredTools: ["dotnet.exe"]);
        string sessionFile = Path.Combine(folder, "session.json"); session.SaveTo(sessionFile);
        check(SessionState.LoadFrom(sessionFile).For(root) is { LastProfile: "Development", RequiredTools.Length: 1 }, "Last profile and team requirements survive restart");
    }
}
