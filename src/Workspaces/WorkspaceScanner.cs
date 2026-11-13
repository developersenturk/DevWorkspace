using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DevWorkspace;

internal sealed record ProjectEntry(
    string Name, string Category, string RelativePath, string Folder, string? EntryFile,
    string Kind, string? Repository, GitStatus Git, ProjectPreference? SavedPreference = null, string? LinkPath = null)
{
    public string Branch => string.IsNullOrWhiteSpace(Git.Branch) ? "—" : Git.Branch;
    public int? ChangedFiles => Git.Changes;
    public ProjectPreference Preference => SavedPreference ?? ProjectPreference.Empty;
    public string Label => Preference.Label ?? "";
    public string ProjectGroupKey => string.IsNullOrWhiteSpace(Preference.Group)
        ? $"project:{RelativePath}" : $"group:{Preference.Group.Trim().ToUpperInvariant()}";
    public bool IsWorkspaceRoot => LinkPath is null && RelativePath == ".";
    public string DisplayPath => IsWorkspaceRoot ? "Çalışma alanı kökü" : LinkPath is null ? RelativePath : EntryFile ?? Folder;
    public string Summary => string.IsNullOrEmpty(Git.Detail) ? Folder : $"{Folder}\n{Git.Detail}";

    public bool HasSameSnapshot(ProjectEntry other) =>
        this with { Git = other.Git, SavedPreference = other.SavedPreference } == other &&
        Git.HasSameSnapshot(other.Git) &&
        Preference with { Commands = other.Preference.Commands } == other.Preference &&
        (Preference.Commands ?? []).SequenceEqual(other.Preference.Commands ?? []);
}

internal sealed record ScanResult(
    IReadOnlyList<ProjectEntry> Projects, int Repositories, int SkippedDirectories, IReadOnlyList<WorkspaceFolder> Folders);

internal static class WorkspaceScanner
{
    private const string AndroidManifest = "AndroidManifest.xml";
    internal const string AndroidKind = "Android";
    private sealed record FolderScan(string Path, string? Repository, bool InProject, FileInfo? ParentSolution = null);
    private sealed record ScanScope(string Category, string Folder, string? Link = null, bool RootOnly = false);
    private static readonly Regex SolutionProjectLine = new(
        "^Project\\(\"(?<type>[^\"]+)\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.CultureInvariant);

    public static async Task<ScanResult> ScanAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var projects = new List<ProjectEntry>();
        var repoFallbacks = new Dictionary<string, ProjectEntry>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new HashSet<string>(workspace.Config.ScanExclusions, StringComparer.OrdinalIgnoreCase) { ".git" };
        string? workspaceRepo = HasGitMarker(workspace.Root) ? workspace.Root : null;
        int skipped = 0;

        WorkspaceFolder[] layout = workspace.GetFolders();
        WorkspaceFolder[] subfolders = workspace.GetExplorerSubfolders();
        var hiddenSubfolders = subfolders.Where(folder => !folder.IsVisible).Select(folder => folder.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] folders = layout.Where(folder => folder.IsVisible).Select(folder => folder.Name).ToArray();
        var scopes = new List<ScanScope> { new("Çalışma alanı", workspace.Root, RootOnly: true) };
        scopes.AddRange(folders.Select(category => new ScanScope(category, Path.Combine(workspace.Root, category))));
        foreach (string link in (workspace.Config.LinkedProjects ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(link) || link.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(link))
                scopes.Add(new("Bağlı projeler", link, link));
            else if (File.Exists(link) && IsProjectFile(link))
            {
                string parent = Path.GetDirectoryName(link)!;
                string kind = KindForFile(link);
                string name = ProjectName(link, Path.GetFileName(parent), kind);
                projects.Add(new(name, "Bağlı projeler", LinkIdentity(link), parent, link,
                    kind, FindGitRoot(parent), new(GitState.None), LinkPath: link));
            }
            else
                projects.Add(new(Path.GetFileName(link), "Bağlı projeler", LinkIdentity(link), link, null,
                    "Bağlantı eksik", null, new(GitState.None), LinkPath: link));
        }

        foreach (ScanScope scope in scopes)
        {
            int initialCount = projects.Count;
            var pending = new Stack<FolderScan>();
            pending.Push(new(scope.Folder, scope.Link is null ? workspaceRepo : FindGitRoot(scope.Folder), false));
            while (pending.TryPop(out FolderScan? current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string folderName = Path.GetFileName(current.Path);
                if (folderName.Length == 0)
                    folderName = workspace.Config.Name;
                if (folderName.StartsWith(GitRecovery.StagingPrefix, StringComparison.OrdinalIgnoreCase) ||
                    folderName.StartsWith(GitHubCli.CloneStagingPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                FileSystemInfo[] entries;
                try
                {
                    var directory = new DirectoryInfo(current.Path);
                    if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        skipped++;
                        continue;
                    }
                    entries = directory.GetFileSystemInfos();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }

                bool isRepoRoot = entries.Any(entry => entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase));
                string? repo = isRepoRoot ? current.Path : current.Repository;
                bool inProject = !isRepoRoot && current.InProject;
                var files = entries.OfType<FileInfo>()
                    .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .ToArray();

                FileInfo? entry = files
                    .Where(file => IsExtension(file, ".slnx", ".sln"))
                    .OrderByDescending(file => Path.GetFileNameWithoutExtension(file.Name).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(file => IsExtension(file, ".slnx"))
                    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                string kind = "Visual Studio";

                if (entry is null && !inProject)
                {
                    entry = files
                        .Where(file => IsExtension(file, ".csproj", ".vbproj", ".fsproj", ".vcxproj"))
                        .OrderByDescending(file => Path.GetFileNameWithoutExtension(file.Name).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                        .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    kind = ".NET / MSBuild";
                    if (entry is null)
                    {
                        entry = files.FirstOrDefault(file => file.Name.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase));
                        kind = "Flutter / Dart";
                    }
                    if (entry is null)
                    {
                        entry = files.FirstOrDefault(file => file.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase));
                        kind = "JavaScript";
                    }
                    if (entry is null)
                    {
                        entry = files.FirstOrDefault(file => file.Name.Equals(AndroidManifest, StringComparison.OrdinalIgnoreCase));
                        kind = AndroidKind;
                    }
                }

                string relativePath = scope.Link is null ? Path.GetRelativePath(workspace.Root, current.Path) : LinkIdentity(current.Path);
                if (entry is not null)
                {
                    bool coveredByParent = scope.Link is null && !isRepoRoot && current.ParentSolution is { } parent &&
                        IsExtension(entry, ".sln") && Path.GetFileNameWithoutExtension(entry.Name)
                            .Equals(Path.GetFileNameWithoutExtension(parent.Name), StringComparison.OrdinalIgnoreCase) &&
                        workspace.PreferenceFor(relativePath) == ProjectPreference.Empty &&
                        string.IsNullOrWhiteSpace(workspace.DescriptionFor(relativePath)) && ContainsSolutionProjects(parent, entry);
                    if (!coveredByParent)
                    {
                        string name = ProjectName(entry.Name, folderName, kind);
                        projects.Add(new(name, scope.Category, relativePath, current.Path, entry.FullName, kind, repo, new(GitState.None), LinkPath: scope.Link));
                    }
                }
                else if (isRepoRoot)
                    repoFallbacks.TryAdd(current.Path, new(folderName, scope.Category, relativePath, current.Path, null, "Git deposu", repo, new(GitState.None), LinkPath: scope.Link));

                if (scope.RootOnly)
                    continue;

                FileInfo? parentSolution = entry is not null && IsExtension(entry, ".slnx") ? entry : null;
                foreach (var child in entries.OfType<DirectoryInfo>())
                {
                    if (!exclusions.Contains(child.Name) &&
                        (scope.Link is not null || !hiddenSubfolders.Contains(Path.GetRelativePath(workspace.Root, child.FullName))))
                        pending.Push(new(child.FullName, repo, inProject || entry is not null, parentSolution));
                }
            }
            if (scope.Link is not null && initialCount == projects.Count && !repoFallbacks.Values.Any(project => project.LinkPath == scope.Link))
                projects.Add(new(Path.GetFileName(scope.Folder), scope.Category, LinkIdentity(scope.Folder), scope.Folder, null,
                    "Proje klasörü", FindGitRoot(scope.Folder), new(GitState.None), LinkPath: scope.Link));
        }

        var representedRepos = projects.Select(project => project.Repository).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        projects.AddRange(repoFallbacks.Where(item => !representedRepos.Contains(item.Key)).Select(item => item.Value));
        projects = projects.OrderBy(project => project.LinkPath is not null)
            .DistinctBy(project => project.EntryFile ?? project.Folder, StringComparer.OrdinalIgnoreCase).ToList();

        projects = projects.Select(project => project with { SavedPreference = workspace.PreferenceFor(project.RelativePath) }).ToList();
        string[] repos = projects.Where(project => !project.Preference.Archived).Select(project => project.Repository)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var statuses = new ConcurrentDictionary<string, GitStatus>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(repos, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (repo, token) => statuses[repo] = await GitStatus.ReadAsync(repo, token));

        var result = projects.Select(project => project.Preference.Archived ? project with { Repository = null } :
                project.Repository is null ? project : project with { Git = statuses[project.Repository] })
            .OrderBy(project => Array.IndexOf(folders, project.Category))
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(project => project.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(result, repos.Length, skipped, [.. layout, .. subfolders]);
    }

    private static bool HasGitMarker(string directory)
    {
        string path = Path.Combine(directory, ".git");
        return Directory.Exists(path) || File.Exists(path);
    }

    private static bool ContainsSolutionProjects(FileInfo outer, FileInfo inner)
    {
        const string solutionFolderType = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
        const string solutionFolderTypeModern = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
        try
        {
            XDocument document = XDocument.Load(outer.FullName);
            if (document.Root is not { } root || root.Name.LocalName != "Solution")
                return false;

            var outerProjects = root.Descendants().Where(element => element.Name.LocalName == "Project")
                .Select(element => (string?)element.Attribute("Path"))
                .OfType<string>().Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path, outer.DirectoryName!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool hasProject = false;
            foreach (string line in File.ReadLines(inner.FullName))
            {
                if (!line.StartsWith("Project(", StringComparison.Ordinal))
                    continue;

                Match match = SolutionProjectLine.Match(line);
                if (!match.Success)
                    return false;
                string type = match.Groups["type"].Value;
                if (type.Equals(solutionFolderType, StringComparison.OrdinalIgnoreCase) ||
                    type.Equals(solutionFolderTypeModern, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!outerProjects.Contains(Path.GetFullPath(match.Groups["path"].Value, inner.DirectoryName!)))
                    return false;
                hasProject = true;
            }
            return hasProject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsExtension(FileInfo file, params string[] extensions) =>
        extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);

    public static bool IsProjectFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".sln" or ".slnx" or ".csproj" or ".vbproj" or ".fsproj" or ".vcxproj" ||
        Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals(AndroidManifest, StringComparison.OrdinalIgnoreCase);

    private static string KindForFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sln" or ".slnx" => "Visual Studio",
        ".json" => "JavaScript",
        ".yaml" => "Flutter / Dart",
        ".xml" => AndroidKind,
        _ => ".NET / MSBuild"
    };

    private static string ProjectName(string file, string folderName, string kind) =>
        kind is "JavaScript" or "Flutter / Dart" or AndroidKind ? folderName : Path.GetFileNameWithoutExtension(file);

    private static string LinkIdentity(string path) =>
        "@linked\\" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));

    private static string? FindGitRoot(string folder)
    {
        for (string? current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (HasGitMarker(current))
                return current;
        return null;
    }
}
