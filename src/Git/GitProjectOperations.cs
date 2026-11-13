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

    public static async Task InitializeAsync(string folder, string projectKind, CancellationToken token)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("Proje klasörü bulunamadı.");

        for (string? current = fullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Bağlantılı bir klasörde Git deposu oluşturulamaz.");
            string git = Path.Combine(current, ".git");
            if (File.Exists(git) || Directory.Exists(git))
                throw new IOException("Proje zaten bir Git deposunun içinde.");
        }

        token.ThrowIfCancellationRequested();
        CreateGitIgnore(fullPath, projectKind);

        var (code, _, error) = await RunAsync(fullPath, ["init", "--quiet", "--", fullPath], token);
        if (code != 0)
            throw new IOException(error.Trim());
        if (!Directory.Exists(Path.Combine(fullPath, ".git")))
            throw new IOException("Git deposunun oluşturulduğu doğrulanamadı.");
    }

    private static void CreateGitIgnore(string folder, string projectKind)
    {
        string path = Path.Combine(folder, ".gitignore");
        if (projectKind == WorkspaceScanner.AndroidKind)
        {
            CreateAndroidGitIgnore(path);
            return;
        }
        if (File.Exists(path))
            return;

        string[] projectPatterns = projectKind switch
        {
            "Visual Studio" or ".NET / MSBuild" => [".vs/", "[Bb]in/", "[Oo]bj/", "*.user", "*.suo"],
            "JavaScript" => ["node_modules/", "npm-debug.log*"],
            "Flutter / Dart" => [".dart_tool/", ".flutter-plugins-dependencies", "build/"],
            _ => []
        };
        string[] patterns = [".DS_Store", "Thumbs.db", .. projectPatterns];
        string temporary = Path.Combine(folder, $".gitignore.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(temporary, patterns);
            File.Move(temporary, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process provided the ignore file before the move.
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void CreateAndroidGitIgnore(string path)
    {
        const string header = "# DevWorkspace Android";
        string[] patterns =
        [
            header, ".DS_Store", "Thumbs.db", ".gradle/", ".idea/", "*.iml", "local.properties",
            "build/*", "!/build/zxing.jar", "/check/classes/", "/check/dex/", "/check/classes.jar",
            "*.apk", "*.apk.idsig", "*.aab", "*.class", "*.dex", "*.jks", "*.keystore"
        ];
        bool exists = File.Exists(path);
        if (exists && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Bağlantılı .gitignore dosyası otomatik güncellenemedi.");
        string existing = exists ? File.ReadAllText(path) : "";
        if (existing.Split('\n').Any(line => line.TrimEnd('\r') == header))
            return;

        string newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string contents = string.Join(newline, patterns) + newline;
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
