using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal static class ProjectDetection
{
    internal const string Android = "Android";
    private const string AndroidManifest = "AndroidManifest.xml";
    private static readonly (string File, string Kind)[] Markers =
    [
        ("pubspec.yaml", "Flutter / Dart"),
        ("ionic.config.json", "Ionic"),
        ("angular.json", "Angular"),
        ("ng-package.json", "Angular"),
        ("project.godot", "Godot"),
        ("tauri.conf.json", "Tauri"), ("tauri.conf.json5", "Tauri"), ("Tauri.toml", "Tauri"),
        ("platformio.ini", "PlatformIO"), ("west.yml", "Zephyr"),
        ("dbt_project.yml", "dbt"), ("Chart.yaml", "Helm"), ("ansible.cfg", "Ansible"),
        ("playbook.yml", "Ansible"), ("playbook.yaml", "Ansible"),
        ("nx.json", "Nx"),
        ("pnpm-workspace.yaml", "JavaScript çalışma alanı"),
        ("deno.json", "Deno"), ("deno.jsonc", "Deno"),
        (AndroidManifest, Android),
        ("pyproject.toml", "Python"), ("Pipfile", "Python"), ("setup.py", "Python"), ("requirements.txt", "Python"),
        ("Cargo.toml", "Rust"),
        ("go.work", "Go"), ("go.mod", "Go"),
        ("pom.xml", "Java / Maven"),
        ("settings.gradle.kts", "Gradle"), ("settings.gradle", "Gradle"),
        ("build.gradle.kts", "Gradle"), ("build.gradle", "Gradle"),
        ("Package.swift", "Swift"), ("Gemfile", "Ruby"), ("composer.json", "PHP"),
        ("mix.exs", "Elixir"), ("build.sbt", "Scala"), ("CMakeLists.txt", "C / C++ (CMake)"),
        ("compose.yaml", "Docker Compose"), ("compose.yml", "Docker Compose"),
        ("docker-compose.yaml", "Docker Compose"), ("docker-compose.yml", "Docker Compose")
    ];
    private static readonly (string Suffix, string Kind)[] Extensions =
    [
        (".uproject", "Unreal Engine"), (".dproj", "Delphi"), (".pro", "Qt"),
        (".ino", "Arduino"), (".kicad_pro", "KiCad"), (".xpr", "FPGA / Vivado"),
        (".qpf", "FPGA / Quartus"), (".tf", "Terraform"), (".tf.json", "Terraform"),
        (".tofu", "OpenTofu"), (".tofu.json", "OpenTofu")
    ];
    private static readonly (string Dependency, string Kind)[] Frameworks =
    [
        ("@ionic/react", "Ionic / React"), ("@ionic/angular", "Ionic / Angular"), ("@ionic/vue", "Ionic / Vue"),
        ("@ionic/core", "Ionic"), ("expo", "Expo"), ("react-native", "React Native"),
        ("next", "Next.js"), ("nuxt", "Nuxt"), ("@sveltejs/kit", "SvelteKit"),
        ("@angular/core", "Angular"), ("astro", "Astro"), ("@remix-run/react", "Remix"),
        ("@nestjs/core", "NestJS"), ("electron", "Electron"), ("@tauri-apps/api", "Tauri"),
        ("@tauri-apps/cli", "Tauri"), ("@capacitor/core", "Capacitor"),
        ("react", "React"), ("vue", "Vue"), ("svelte", "Svelte"), ("solid-js", "SolidJS"),
        ("vite", "Vite")
    ];
    private static readonly string[] DependencySections = ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"];
    internal static readonly string[] GeneratedFolders =
        ["node_modules", ".dart_tool", ".gradle", ".venv", "__pycache__", ".next", ".nuxt", ".svelte-kit", ".yarn", ".angular",
         ".godot", ".terraform", ".ipynb_checkpoints", ".pio", "DerivedData"];

    internal static bool IsJavaScript(string? entry) =>
        string.Equals(Path.GetFileName(entry), "package.json", StringComparison.OrdinalIgnoreCase);

    internal static bool IsManifest(string file) => IsJavaScript(file) ||
        Markers.Any(marker => marker.File.Equals(file, StringComparison.OrdinalIgnoreCase)) ||
        ExtensionKind(file) is not null || file.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase);

    private static string? ExtensionKind(string file) =>
        Extensions.FirstOrDefault(item => file.EndsWith(item.Suffix, StringComparison.OrdinalIgnoreCase)).Kind;

    internal static (FileInfo? Entry, string Kind) Detect(string folder, FileInfo[] files, string? parentKind)
    {
        FileInfo? Find(string name) => files.FirstOrDefault(file => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (Find("pubspec.yaml") is { } pubspec)
            return (pubspec, "Flutter / Dart");
        if (Find("wp-load.php") is { } wordpress && Directory.Exists(Path.Combine(folder, "wp-includes")))
            return (wordpress, "WordPress");
        if (Find("package.json") is { } package)
            return (package, JavaScriptKind(package.FullName, Find("ionic.config.json") is not null,
                Find("angular.json") is not null || Find("ng-package.json") is not null,
                Find("nx.json") is not null, Find("pnpm-workspace.yaml") is not null));

        foreach (var (name, kind) in Markers)
        {
            if (Find(name) is not { } file)
                continue;
            if (name == AndroidManifest && (parentKind is Android || Path.GetFileName(folder) is "main" or "debug" or "release"))
                continue;
            if (kind == "Gradle")
            {
                if (parentKind == Android || IsMobileHost(folder, parentKind))
                    continue;
                if (IsAndroidGradleFolder(folder))
                    return (file, Android);
            }
            return (file, RefineKind(file, kind));
        }
        var manifest = files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(file => ExtensionKind(file.Name) is not null);
        if (manifest is not null)
            return (manifest, ExtensionKind(manifest.Name)!);
        var notebook = files.FirstOrDefault(file => file.Extension.Equals(".ipynb", StringComparison.OrdinalIgnoreCase));
        if (notebook is not null)
            return (notebook, "Jupyter");
        return (null, "");
    }

    internal static (FileInfo Entry, string Kind)? DetectSpecial(string folder, FileInfo[] files, DirectoryInfo[] directories, string? parentKind)
    {
        var unity = new FileInfo(Path.Combine(folder, "ProjectSettings", "ProjectVersion.txt"));
        if (unity.Exists && Directory.Exists(Path.Combine(folder, "Assets")))
            return (unity, "Unity");
        if (files.FirstOrDefault(file => file.Name.Equals("project.godot", StringComparison.OrdinalIgnoreCase)) is { } godot)
            return (godot, "Godot");
        if (parentKind is not ("Flutter / Dart" or "React Native" or "Expo" or "Capacitor") &&
            parentKind?.StartsWith("Ionic", StringComparison.Ordinal) != true)
        {
            foreach (var (bundleExtension, marker) in new[] { (".xcworkspace", "contents.xcworkspacedata"), (".xcodeproj", "project.pbxproj") })
            {
                foreach (var bundle in directories.Where(item => item.Extension.Equals(bundleExtension, StringComparison.OrdinalIgnoreCase)
                    && !item.Attributes.HasFlag(FileAttributes.ReparsePoint)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = new FileInfo(Path.Combine(bundle.FullName, marker));
                    if (entry.Exists)
                        return (entry, "Xcode");
                    if (bundleExtension == ".xcodeproj" && TryFindXcodeConfiguration(bundle) is { } configuration)
                        return (configuration, "Xcode");
                }
            }
        }
        foreach (string name in new[] { "tauri.conf.json", "tauri.conf.json5", "Tauri.toml" })
        {
            var config = new FileInfo(Path.Combine(folder, "src-tauri", name));
            if (config.Exists)
                return (files.FirstOrDefault(file => IsJavaScript(file.Name)) ?? config, "Tauri");
        }
        var unreal = files.Where(file => ExtensionKind(file.Name) == "Unreal Engine")
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return unreal is null ? null : (unreal, "Unreal Engine");
    }

    private static FileInfo? TryFindXcodeConfiguration(DirectoryInfo bundle)
    {
        try { return bundle.EnumerateFiles("*.xcproj").OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string RefineKind(FileInfo file, string kind)
    {
        string folder = file.DirectoryName!;
        string[] evidence = kind switch
        {
            "Python" => ["django", "fastapi"],
            "Java / Maven" or "Gradle" => ["spring-boot-starter-parent", "spring-boot-starter", "org.springframework.boot"],
            "PHP" => ["laravel/framework"],
            "Ruby" => ["rails"],
            "C / C++ (CMake)" => ["IDF_PATH", "find_package(Zephyr", "find_package(Qt"],
            _ => []
        };
        if (evidence.Length == 0)
            return kind;
        try
        {
            if (file.Length > 1_000_000)
                return kind;
            string text = File.ReadAllText(file.FullName);
            string? match = evidence.FirstOrDefault(value => Regex.IsMatch(text,
                $@"(?<![\w.-]){Regex.Escape(value)}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            return match?.ToLowerInvariant() switch
            {
                "django" => "Django", "fastapi" => "FastAPI",
                "spring-boot-starter-parent" or "spring-boot-starter" or "org.springframework.boot" => "Spring Boot",
                "laravel/framework" => "Laravel", "rails" => "Rails",
                "idf_path" => "ESP-IDF", "find_package(zephyr" => "Zephyr", "find_package(qt" => "Qt",
                _ when kind == "Python" && File.Exists(Path.Combine(folder, "manage.py")) => "Django",
                _ => kind
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return kind; }
    }

    internal static string KindForManifest(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        if (IsJavaScript(path))
            return HasTauriConfig(folder) ? "Tauri" : JavaScriptKind(path, File.Exists(Path.Combine(folder, "ionic.config.json")),
                File.Exists(Path.Combine(folder, "angular.json")) || File.Exists(Path.Combine(folder, "ng-package.json")),
                File.Exists(Path.Combine(folder, "nx.json")), File.Exists(Path.Combine(folder, "pnpm-workspace.yaml")));
        string name = Path.GetFileName(path);
        string kind = Markers.FirstOrDefault(marker => marker.File.Equals(name, StringComparison.OrdinalIgnoreCase)).Kind ??
            ExtensionKind(name) ?? "Jupyter";
        return kind == "Gradle" && IsAndroidGradleFolder(folder) ? Android : RefineKind(new(path), kind);
    }

    private static bool HasTauriConfig(string folder) =>
        new[] { "tauri.conf.json", "tauri.conf.json5", "Tauri.toml" }
            .Any(name => File.Exists(Path.Combine(folder, "src-tauri", name)));

    internal static bool IsProjectMetadataFolder(DirectoryInfo directory, string? parentKind) =>
        directory.Extension.Equals(".xcodeproj", StringComparison.OrdinalIgnoreCase) ||
        directory.Extension.Equals(".xcworkspace", StringComparison.OrdinalIgnoreCase) ||
        parentKind == "Tauri" && directory.Name.Equals("src-tauri", StringComparison.OrdinalIgnoreCase) ||
        parentKind == "Unity" && directory.Name is "Library" or "Temp" or "Obj" or "Logs" or "ProjectSettings" ||
        parentKind == "Unreal Engine" && directory.Name is "Binaries" or "Intermediate" or "Saved" or "DerivedDataCache";

    private static bool IsAndroidGradleFolder(string folder) =>
        File.Exists(Path.Combine(folder, "app", "src", "main", AndroidManifest)) ||
        File.Exists(Path.Combine(folder, "src", "main", AndroidManifest));

    internal static bool IsMobileHost(string folder, string? parentKind) =>
        Path.GetFileName(folder).Equals("android", StringComparison.OrdinalIgnoreCase) &&
        (parentKind is "Flutter / Dart" or "Expo" or "React Native" or "Capacitor" ||
         parentKind?.StartsWith("Ionic", StringComparison.Ordinal) == true);

    private static string JavaScriptKind(string path, bool ionic, bool angular, bool nx, bool workspace)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (new FileInfo(path).Length <= 1_000_000)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (string section in DependencySections)
                    {
                        if (json.RootElement.TryGetProperty(section, out var group) && group.ValueKind == JsonValueKind.Object)
                            dependencies.UnionWith(group.EnumerateObject().Select(item => item.Name));
                    }
                    workspace |= json.RootElement.TryGetProperty("workspaces", out var workspaces) &&
                        workspaces.ValueKind is JsonValueKind.Array or JsonValueKind.Object;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        if (ionic)
            return dependencies.Contains("@ionic/react") || dependencies.Contains("react") ? "Ionic / React" :
                angular || dependencies.Contains("@ionic/angular") || dependencies.Contains("@angular/core") ? "Ionic / Angular" :
                dependencies.Contains("@ionic/vue") || dependencies.Contains("vue") ? "Ionic / Vue" : "Ionic";
        if (angular && !dependencies.Contains("@ionic/angular"))
            return "Angular";
        if (nx)
            return "Nx";
        if (workspace)
            return "JavaScript çalışma alanı";
        return Frameworks.FirstOrDefault(framework => dependencies.Contains(framework.Dependency)).Kind ?? "JavaScript";
    }
}
