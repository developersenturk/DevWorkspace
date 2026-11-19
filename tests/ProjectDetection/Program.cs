using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Collections;

// Exercise the actual scanner without exposing application internals as public API.
var assembly = Assembly.Load("DevWorkspace");
Type Type(string name) => assembly.GetType("DevWorkspace." + name, throwOnError: true)!;
object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
var root = Path.Combine(Path.GetTempPath(), "DevWorkspace-detection-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    void Write(string path, string text)
    {
        path = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    Write("Projects/Controls/Controls.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    Write("Projects/Controls/Reporting/Hosting/package.json", """{"private":true,"dependencies":{"jquery":"3.7.1"}}""");
    Write("Projects/Controls/Other/Assets/package.json", """{"private":true,"devDependencies":{"some-assets":"1.0.0"}}""");
    Write("Projects/Controls/Apps/Standalone/package.json", """{"private":true,"scripts":{"start":"vite"},"dependencies":{"vite":"1.0.0"}}""");
    Write("Projects/Controls/Apps/Source/package.json", """{"private":true,"dependencies":{"react":"1.0.0"}}""");
    Write("Projects/Controls/Apps/Source/src/index.js", "");
    Write("Projects/Controls/Public/package.json", """{"dependencies":{"jquery":"3.7.1"}}""");
    Write("Projects/Controls/Workspace/package.json", """{"private":true,"workspaces":[],"dependencies":{"jquery":"3.7.1"}}""");
    Write("Projects/Controls/Broken/package.json", "{");
    Write("Projects/Controls/IndependentRepo/.git/HEAD", "ref: refs/heads/main");
    Write("Projects/Controls/IndependentRepo/package.json", """{"private":true,"dependencies":{"jquery":"3.7.1"}}""");
    Write("Projects/Explicit/Explicit.csproj", """
        <Project><PropertyGroup><SpaRoot>Deep/Client/</SpaRoot></PropertyGroup></Project>
        """);
    Write("Projects/Explicit/Deep/Client/package.json", """{"scripts":{"build":"vite build"}}""");
    Write("Projects/Exec/Exec.vbproj", """
        <Project><Target Name="BuildClient"><Exec Command="pnpm install" WorkingDirectory="$(MSBuildProjectDirectory)/Deep/Client/" /></Target></Project>
        """);
    Write("Projects/Exec/Deep/Client/package.json", """{"scripts":{"build":"vite build"}}""");
    Write("Projects/NoOwner/Assets/package.json", """{"private":true,"dependencies":{"jquery":"3.7.1"}}""");
    Write("Projects/Controls/Preferred/package.json", """{"private":true,"dependencies":{"jquery":"3.7.1"}}""");

    var configJson = JsonSerializer.Serialize(new
    {
        SchemaVersion = 1, Name = "Tests", Folders = new[] { "Projects" }, ScanExclusions = Array.Empty<string>(),
        ProjectDescriptions = new Dictionary<string, string> { ["Projects/Controls/Preferred".Replace('/', Path.DirectorySeparatorChar)] = "Explicitly listed" }
    });
    var config = JsonSerializer.Deserialize(configJson, Type("WorkspaceConfig"))!;
    var workspace = Activator.CreateInstance(Type("Workspace"), root, config)!;
    var scan = (Task)Type("WorkspaceScanner").GetMethod("ScanAsync")!.Invoke(null, [workspace, CancellationToken.None])!;
    await scan;
    var result = Property(scan, "Result")!;
    var projects = ((IEnumerable)Property(result, "Projects")!).Cast<object>().ToArray();
    var components = ((IEnumerable)Property(result, "Components")!).Cast<object>().ToArray();
    int passed = 0;
    void Expect(string path, bool component, string? owner = null)
    {
        string folder = Path.GetFullPath(Path.Combine(root, path));
        bool listed = projects.Any(project => (string)Property(project, "Folder")! == folder);
        var match = components.SingleOrDefault(item => (string)Property(item, "Folder")! == folder);
        if (listed == component || (match is not null) != component ||
            owner is not null && (string?)Property(match!, "OwnerFolder") != Path.GetFullPath(Path.Combine(root, owner)))
            throw new Exception($"Unexpected classification: {path} (project={listed}, component={match is not null})");
        Console.WriteLine($"PASS {path}: {(component ? "component" : "project")}");
        passed++;
    }
    Expect("Projects/Controls/Reporting/Hosting", true, "Projects/Controls");
    Expect("Projects/Controls/Other/Assets", true, "Projects/Controls");
    Expect("Projects/Explicit/Deep/Client", true, "Projects/Explicit");
    Expect("Projects/Exec/Deep/Client", true, "Projects/Exec");
    foreach (string path in new[] { "Apps/Standalone", "Apps/Source", "Public", "Workspace", "Broken", "IndependentRepo", "Preferred" })
        Expect("Projects/Controls/" + path, false);
    Expect("Projects/NoOwner/Assets", false);
    Console.WriteLine($"{passed} scanner checks passed.");

    if (args.Length == 3)
    {
        var realWorkspace = Type("Workspace").GetMethod("Open")!.Invoke(null, [args[0]])!;
        var realScan = (Task)Type("WorkspaceScanner").GetMethod("ScanAsync")!.Invoke(null, [realWorkspace, CancellationToken.None])!;
        await realScan;
        var realResult = Property(realScan, "Result")!;
        var realProjects = ((IEnumerable)Property(realResult, "Projects")!).Cast<object>();
        var realComponents = ((IEnumerable)Property(realResult, "Components")!).Cast<object>();
        string expectedFolder = Path.GetFullPath(args[1]);
        string expectedOwner = Path.GetFullPath(args[2]);
        if (realProjects.Any(item => string.Equals((string?)Property(item, "Folder"), expectedFolder, StringComparison.OrdinalIgnoreCase)) ||
            !realComponents.Any(item => string.Equals((string?)Property(item, "Folder"), expectedFolder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string?)Property(item, "OwnerFolder"), expectedOwner, StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Real workspace component classification failed: " + expectedFolder);
        Console.WriteLine($"PASS real workspace: {expectedFolder} belongs to {expectedOwner}");
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}
