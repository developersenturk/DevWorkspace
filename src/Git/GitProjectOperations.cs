using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevWorkspace;

internal sealed record GitCommit(string Hash, string Date, string Author, string Subject);

internal static class GitProjectOperations
{
    public static async Task<IReadOnlyList<GitCommit>> HistoryAsync(string repository, CancellationToken token)
    {
        var (code, output, error) = await RunAsync(repository,
            ["log", "-z", "--max-count=100", "--no-show-signature", "--format=%h%x1f%as%x1f%an%x1f%s", "--"], token);
        if (code != 0)
        {
            if (error.Contains("does not have any commits yet", StringComparison.OrdinalIgnoreCase))
                return [];
            throw new IOException(error.Trim());
        }

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimStart('\r', '\n').Split('\u001f', 4))
            .Where(parts => parts.Length == 4)
            .Select(parts => new GitCommit(parts[0], parts[1], parts[2], parts[3])).ToArray();
    }

    public static async Task InitializeAsync(string folder, string projectKind, CancellationToken token, string? entryFile = null)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(Loc.Text("LAC678EB688A2"));

        for (string? current = fullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(Loc.Text("L18B53AF26112"));
            string git = Path.Combine(current, ".git");
            if (File.Exists(git) || Directory.Exists(git))
                throw new IOException(Loc.Text("L5E087E1CA16F"));
        }

        token.ThrowIfCancellationRequested();
        CreateGitIgnore(fullPath, projectKind, entryFile);

        var (code, _, error) = await RunAsync(fullPath, ["init", "--quiet", "--", fullPath], token);
        if (code != 0)
            throw new IOException(error.Trim());
        if (!Directory.Exists(Path.Combine(fullPath, ".git")))
            throw new IOException(Loc.Text("L88AEB9D7C58F"));
    }

    private static void CreateGitIgnore(string folder, string projectKind, string? entryFile)
    {
        string path = Path.Combine(folder, ".gitignore");
        string[] projectPatterns = projectKind switch
        {
            ProjectDetection.Android => [".gradle/", ".idea/", "*.iml", "local.properties", "build/*", "!/build/zxing.jar",
                "/check/classes/", "/check/dex/", "/check/classes.jar", "*.apk", "*.apk.idsig", "*.aab", "*.class", "*.dex", "*.jks", "*.keystore"],
            "Visual Studio" or ".NET / MSBuild" => [".vs/", "[Bb]in/", "[Oo]bj/", "*.user", "*.suo"],
            "Flutter / Dart" => [".dart_tool/", ".flutter-plugins-dependencies", "build/"],
            "Unity" => ["/Library/", "/Temp/", "/Obj/", "/Logs/", "/UserSettings/", "*.csproj", "*.sln"],
            "Unreal Engine" => ["/Binaries/", "/Intermediate/", "/Saved/", "/DerivedDataCache/"],
            "Godot" => ["/.godot/", "/.import/"],
            "Tauri" => ["target/"],
            "Xcode" or "Swift" => ["DerivedData/", "xcuserdata/", "*.xcuserstate", ".build/"],
            "Qt" or "C / C++ (CMake)" => ["build/", "CMakeFiles/", "CMakeCache.txt", "*.pro.user*"],
            "Delphi" => ["__history/", "__recovery/", "*.dcu", "*.dproj.local", "*.identcache", "*.exe"],
            "Python" or "Django" or "FastAPI" or "dbt" or "Jupyter" or "Ansible" =>
                ["__pycache__/", "*.py[cod]", ".venv/", ".pytest_cache/", ".ipynb_checkpoints/", "/target/", "/dbt_packages/", "/logs/", "*.retry"],
            "Java / Maven" or "Gradle" or "Spring Boot" or "Scala" => ["target/", ".gradle/", "build/", "*.class"],
            "PHP" or "Laravel" => ["vendor/", ".env", "storage/logs/*.log"],
            "WordPress" => ["wp-config.php", "/wp-content/uploads/", "/wp-content/cache/"],
            "Ruby" or "Rails" => ["/.bundle/", "/vendor/bundle/", "/log/*.log", "/tmp/", "/config/master.key"],
            "Rust" => ["target/"],
            "Go" => ["*.test", "coverage.out"],
            "Elixir" => ["/_build/", "/deps/", "/erl_crash.dump"],
            "Terraform" or "OpenTofu" => [".terraform/", "*.tfstate", "*.tfstate.*", "*.tfplan", "crash.log"],
            "Helm" => ["/charts/*.tgz"],
            "Docker Compose" => [".env"],
            "Arduino" or "PlatformIO" or "ESP-IDF" or "Zephyr" => [".pio/", "build/", ".west/", "managed_components/", "sdkconfig.old"],
            "KiCad" => ["*-backups/", "*.kicad_prl", "~*.lck", "fp-info-cache"],
            "FPGA / Vivado" => ["*.cache/", "*.runs/", "*.sim/", "*.hw/", "*.gen/", "*.jou", "vivado*.log"],
            "FPGA / Quartus" => ["db/", "incremental_db/", "output_files/", "*.qws"],
            _ => []
        };
        string[] javascript = ProjectDetection.IsJavaScript(entryFile) || projectKind == "JavaScript"
            ? ["node_modules/", "npm-debug.log*", "dist/", ".next/", ".nuxt/", ".svelte-kit/", ".angular/"] : [];
        const string header = "# DevWorkspace";
        string[] patterns = [header, ".DS_Store", "Thumbs.db", .. projectPatterns, .. javascript];
        bool exists = File.Exists(path);
        if (exists && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException(Loc.Text("L9E7D12299DA6"));
        string existing = exists ? File.ReadAllText(path) : "";
        var existingLines = existing.Split('\n').Select(line => line.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        string[] missing = patterns.Skip(1).Where(pattern => !existingLines.Contains(pattern)).Distinct().ToArray();
        if (missing.Length == 0)
            return;

        string newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string contents = string.Join(newline, new[] { header }.Concat(missing)) + newline;
        if (existing.Length > 0)
            contents += newline + existing;

        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: exists);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static async Task<(int Code, string Output, string Error)> RunAsync(string folder, string[] arguments,
        CancellationToken token, int timeoutSeconds = 12, bool promptForSsh = false)
    {
        var startInfo = CreateStartInfo(folder, arguments);
        if (promptForSsh)
            SshAskPass.Configure(startInfo);
        return await ProjectTools.CaptureAsync(startInfo, "Git", timeoutSeconds, token).ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateStartInfo(string folder, IEnumerable<string> arguments)
    {
        var startInfo = ProjectTools.CreateCaptureInfo("git.exe", folder,
            new[] { "--no-pager", "--no-optional-locks" }.Concat(arguments));
        foreach (string variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
                     "GIT_TEMPLATE_DIR", "GIT_NAMESPACE", "GIT_PREFIX", "GIT_LITERAL_PATHSPECS", "GIT_GLOB_PATHSPECS",
                     "GIT_NOGLOB_PATHSPECS", "GIT_ICASE_PATHSPECS" })
            startInfo.Environment.Remove(variable);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["LC_ALL"] = "C";
        return startInfo;
    }
}
