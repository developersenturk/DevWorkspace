using System.IO;
using System.Text.Json;
using DevWorkspace;

Loc.Initialize("en");
int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
void Reject(Action action, string label)
{
    try { action(); } catch (InvalidDataException) { checks++; return; }
    throw new Exception("Accepted " + label);
}
string temporary = Path.Combine(Path.GetTempPath(), "DevWorkspace sharing " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    ProjectEntry Project(string root)
    {
        string folder = Path.Combine(root, "src", "demo"); Directory.CreateDirectory(folder);
        string entry = Path.Combine(folder, "demo.csproj");
        File.WriteAllText(entry, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return new("<script>alert('x')</script>", "", "src/demo/demo.csproj", folder, entry, ".NET", root, new(GitState.Changed, "private-branch", 3));
    }
    string source = Path.Combine(temporary, "Alice"), target = Path.Combine(temporary, "Bob");
    var alice = Project(source); var bob = Project(target);
    var command = ProjectTools.Presets(alice).Last();
    var profile = new LaunchProfile("Team build", [new(alice.RelativePath, command), new(alice.RelativePath, null)]);
    string json = WorkspaceSharing.ExportProfile(profile, [alice], source);
    Check(!json.Contains("Alice"), "Export leaks source root");
    var restored = WorkspaceSharing.ImportProfile(json, [bob], target);
    Check(restored.Steps.Length == 2 && restored.Steps[1].Command is null, "Profile steps changed");
    Check(restored.Steps[0].Command!.Arguments.Contains("Bob") && !restored.Steps[0].Command!.Arguments.Contains("Alice"), "Commands not rebound to target workspace");
    var terminal = profile with { Steps = [new(alice.RelativePath, null, "terminal")] };
    Check(WorkspaceSharing.ImportProfile(WorkspaceSharing.ExportProfile(terminal, [alice], source), [bob], target).Steps[0].Action == "terminal", "Terminal action lost during sharing");
    Reject(() => WorkspaceSharing.ExportProfile(profile with { Steps = [new(alice.RelativePath, new("evil", "cmd.exe", "/c echo untrusted"))] }, [alice], source), "custom command export");
    Reject(() => WorkspaceSharing.ExportProfile(profile, [alice with { LinkPath = "C:/private" }], source), "linked project export");
    Reject(() => WorkspaceSharing.ImportProfile(json, [], target), "missing project");
    string Payload(string project, string? preset = null, int version = 1) => JsonSerializer.Serialize(new { schemaVersion = version, name = "team", steps = new[] { new { project, preset } } });
    foreach (string path in new[] { "../private", "src/../private", "C:/private", "\\\\server\\secret", "/private", "src\\..\\private" })
        Reject(() => WorkspaceSharing.ImportProfile(Payload(path), [bob], target), path);
    Reject(() => WorkspaceSharing.ImportProfile(Payload(bob.RelativePath, "cmd.exe /c echo untrusted"), [bob], target), "unknown preset");
    Reject(() => WorkspaceSharing.ImportProfile(Payload(bob.RelativePath, version: 2), [bob], target), "future schema");
    Reject(() => WorkspaceSharing.ImportProfile("{\"schemaVersion\":1,\"name\":\"x\",\"steps\":[null]}", [bob], target), "null step");
    Reject(() => WorkspaceSharing.ImportProfile(new string(' ', 1024 * 1024 + 1), [bob], target), "oversized profile");
    string report = WorkspaceSharing.Report([alice], false);
    Check(!report.Contains("<script>") && !report.Contains("alert") && !report.Contains("Alice") && !report.Contains("private-branch"), "Anonymous report leaks metadata");
    Check(report.Contains("Project 1") && report.Contains("3</td>"), "Report missing snapshot");
    Check(WorkspaceSharing.Report([alice], true).Contains("&lt;script&gt;"), "Report does not escape names");
    var session = new SessionState(); UsageMetrics.Record(session, "app_open");
    Check(session.UsageCounters.Count == 0, "Metrics enabled by default");
    session.UsageMetricsEnabled = true;
    UsageMetrics.Record(session, "app_open"); UsageMetrics.Record(session, "C:/private");
    Check(session.UsageCounters.Count == 1 && session.UsageCounters["app_open"] == 1, "Metrics allow non-whitelisted values");
    session.UsageCounters["app_open"] = long.MaxValue; UsageMetrics.Record(session, "app_open");
    Check(session.UsageCounters["app_open"] == long.MaxValue, "Counter overflow");
    session.UsageMetricsEnabled = false; UsageMetrics.Record(session, "workspace_open");
    Check(!session.UsageCounters.ContainsKey("workspace_open"), "Opt out still records");
    string destination = Path.Combine(temporary, "report.html");
    WorkspaceSharing.WriteAtomic(destination, report); WorkspaceSharing.WriteAtomic(destination, "replacement");
    Check(File.ReadAllText(destination) == "replacement" && !Directory.EnumerateFiles(temporary, "*.tmp").Any(), "Atomic write failed");
}
finally { Directory.Delete(temporary, true); }
Console.WriteLine($"{checks} workspace sharing, report privacy and local metrics checks passed.");
