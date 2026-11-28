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
    Write("Projects/ModelAmiral/ModelAmiral.slnx", """
        <Solution><Project Path="Center/Center.csproj" /><Project Path="LicenseService/LicenseService.csproj" /></Solution>
        """);
    foreach (string host in new[] { "Center", "LicenseService" })
    {
        Write($"Projects/ModelAmiral/{host}/{host}.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web"><Target Name="BuildClient" BeforeTargets="PrepareForBuild">
              <Exec WorkingDirectory="ClientApp" Command="node &quot;$(MSBuildProjectDirectory)/../Build/Run-ClientBuild.mjs&quot; build" />
            </Target></Project>
            """);
        Write($"Projects/ModelAmiral/{host}/ClientApp/package.json", """{"private":true,"scripts":{"build":"vite build --outDir ../wwwroot"},"dependencies":{"react":"19.2.0"}}""");
        Write($"Projects/ModelAmiral/{host}/ClientApp/src/index.js", "");
    }
    Write("Projects/NodeExe/NodeExe.csproj", """
        <Project><Target Name="BuildClient"><Exec Command="node.exe build.js" WorkingDirectory="ClientApp" /></Target></Project>
        """);
    Write("Projects/NodeExe/ClientApp/package.json", """{"scripts":{"build":"vite build"}}""");
    Write("Projects/NodeExe/Independent/package.json", """{"scripts":{"build":"vite build"}}""");
    Write("Projects/NoOwner/Assets/package.json", """{"private":true,"dependencies":{"jquery":"3.7.1"}}""");
    Write("Projects/Controls/Preferred/package.json", """{"private":true,"dependencies":{"jquery":"3.7.1"}}""");
    Write("Projects/Product/Production.csproj", "<Project />");
    Write("Projects/Product/tests/ConsoleRunner/Runner.csproj", "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
    Write("Projects/Product/__tests__/Client/package.json", "{\"scripts\":{\"start\":\"vite\"}}");
    Write("Projects/Named/Product.Tests.csproj", "<Project />");
    Write("Projects/Flagged/Checks.csproj", "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
    Write("Projects/Sdk/Checks.csproj", "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>");
    Write("Projects/Framework/Checks.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
    Write("Projects/Latest/Latest.csproj", "<Project />");
    Write("Projects/Contests/Contests.csproj", "<Project />");
    Write("LinkedChecks/Checks.csproj", "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
    Write("OnlyChecks/Checks.csproj", "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
    Write("OnlyChecks/Applications.slnx", "<Solution><Project Path=\"Checks.csproj\" /></Solution>");

    var configJson = JsonSerializer.Serialize(new
    {
        SchemaVersion = 1, Name = "Tests", Folders = new[] { "Projects" }, ScanExclusions = Array.Empty<string>(),
        LinkedProjects = new[] { Path.Combine(root, "LinkedChecks"), Path.Combine(root, "Projects/Flagged/Checks.csproj"), Path.Combine(root, "OnlyChecks/Applications.slnx") },
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
    Expect("Projects/ModelAmiral", false);
    Expect("Projects/ModelAmiral/Center/ClientApp", true, "Projects/ModelAmiral");
    Expect("Projects/ModelAmiral/LicenseService/ClientApp", true, "Projects/ModelAmiral");
    Expect("Projects/NodeExe/ClientApp", true, "Projects/NodeExe");
    Expect("Projects/NodeExe/Independent", false);
    foreach (string path in new[] { "Apps/Standalone", "Apps/Source", "Public", "Workspace", "Broken", "IndependentRepo", "Preferred" })
        Expect("Projects/Controls/" + path, false);
    Expect("Projects/NoOwner/Assets", false);
    foreach (string path in new[] { "Projects/Product", "Projects/Latest", "Projects/Contests" }) Expect(path, false);
    foreach (string path in new[] { "Projects/Product/tests/ConsoleRunner", "Projects/Product/__tests__/Client", "Projects/Named", "Projects/Flagged", "Projects/Sdk", "Projects/Framework", "LinkedChecks", "OnlyChecks" })
    {
        string folder = Path.GetFullPath(Path.Combine(root, path));
        if (projects.Any(p => (string)Property(p, "Folder")! == folder) || components.Any(p => (string)Property(p, "Folder")! == folder))
            throw new Exception("Test project should be hidden: " + path);
        passed++;
    }
    if (!File.Exists(Path.Combine(root, "Projects/Product/tests/ConsoleRunner/Runner.csproj"))) throw new Exception("Test sources must remain intact");
    Console.WriteLine($"{passed} scanner checks passed.");

    if (args is ["--hide-tests", var actualRoot])
    {
        var realWorkspace = Type("Workspace").GetMethod("Open")!.Invoke(null, [actualRoot])!;
        var realScan = (Task)Type("WorkspaceScanner").GetMethod("ScanAsync")!.Invoke(null, [realWorkspace, CancellationToken.None])!;
        await realScan;
        var realResult = Property(realScan, "Result")!;
        var actualProjects = ((IEnumerable)Property(realResult, "Projects")!).Cast<object>().ToArray();
        bool IsTest(object item) => Property(item, "EntryFile") is string file && (bool)Type("TestProjectDetection").GetMethod("IsTestProject", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [file])!;
        if (actualProjects.Any(IsTest) || actualProjects.Length == 0) throw new Exception("Actual workspace visibility verification failed");
        Console.WriteLine($"PASS actual workspace: {actualProjects.Length} production projects; test projects hidden.");
    }

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
