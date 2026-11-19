using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DevWorkspace;

internal sealed record ProjectEntry(
    string Name, string Category, string RelativePath, string Folder, string? EntryFile,
    string Kind, string? Repository, GitStatus Git, ProjectPreference? SavedPreference = null, string? LinkPath = null,
    string? OwnerName = null, string? OwnerRelativePath = null, string? OwnerGroupKey = null)
{
    public string ListName => OwnerName is null ? Name : Loc.Format("LD6793EA727E9", Name, OwnerName);
    public string Branch => string.IsNullOrWhiteSpace(Git.Branch) ? "—" : Git.Branch;
    public int? ChangedFiles => Git.Changes;
    public ProjectPreference Preference => SavedPreference ?? ProjectPreference.Empty;
    public string Label => Preference.Label ?? "";
    public string LabelDisplay => Loc.Label(Label);
    public string ProjectGroupKey => OwnerGroupKey ?? (string.IsNullOrWhiteSpace(Preference.Group)
        ? $"project:{RelativePath}" : $"group:{Preference.Group.Trim().ToUpperInvariant()}");
    public bool IsWorkspaceRoot => LinkPath is null && RelativePath == ".";
    public string DisplayPath => IsWorkspaceRoot ? Loc.Text("L8305835D3AFB") : LinkPath is null ? RelativePath : EntryFile ?? Folder;
    public string Summary => string.IsNullOrEmpty(Git.Detail) ? Folder : $"{Folder}\n{Git.Detail}";

    public bool HasSameSnapshot(ProjectEntry other) =>
        this with { Git = other.Git, SavedPreference = other.SavedPreference } == other &&
        Git.HasSameSnapshot(other.Git) &&
        Preference with { Commands = other.Preference.Commands } == other.Preference &&
        (Preference.Commands ?? []).SequenceEqual(other.Preference.Commands ?? []);
}

internal sealed record ProjectComponent(string OwnerFolder, string Folder, string Name, string Kind, string Role);

internal sealed record ScanResult(
    IReadOnlyList<ProjectEntry> Projects, int Repositories, int SkippedDirectories, IReadOnlyList<WorkspaceFolder> Folders,
    IReadOnlyList<ProjectComponent> Components);

internal static class WorkspaceScanner
{
    private sealed record FolderScan(string Path, string? Repository, bool InDotNetProject,
        FileInfo? ParentSolution = null, string? ParentKind = null, bool InProjectScripts = false, bool InMobileHost = false,
        string? OwnerFolder = null);
    private sealed record ScanScope(string Category, string Folder, string? Link = null, bool RootOnly = false);
    private static readonly Regex SolutionProjectLine = new(
        "^Project\\(\"(?<type>[^\"]+)\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.CultureInvariant);

    public static async Task<ScanResult> ScanAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        var projects = new List<ProjectEntry>();
        var components = new List<ProjectComponent>();
        var testOnlyFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repoFallbacks = new Dictionary<string, ProjectEntry>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new HashSet<string>(workspace.Config.ScanExclusions, StringComparer.OrdinalIgnoreCase) { ".git" };
        exclusions.UnionWith(ProjectDetection.GeneratedFolders);
        string? workspaceRepo = HasGitMarker(workspace.Root) ? workspace.Root : null;
        int skipped = 0;

        WorkspaceFolder[] layout = workspace.GetFolders();
        WorkspaceFolder[] subfolders = workspace.GetExplorerSubfolders();
        var hiddenSubfolders = subfolders.Where(folder => !folder.IsVisible).Select(folder => folder.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] folders = layout.Where(folder => folder.IsVisible).Select(folder => folder.Name).ToArray();
        var scopes = new List<ScanScope> { new(Loc.Text("L5321550F56C9"), workspace.Root, RootOnly: true) };
        scopes.AddRange(folders.Where(category => !exclusions.Contains(category))
            .Select(category => new ScanScope(category, Path.Combine(workspace.Root, category))));
        foreach (string link in (workspace.Config.LinkedProjects ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(link) || link.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(link))
                scopes.Add(new(Loc.Text("L68F3F11503E7"), link, link));
            else if (File.Exists(link) && IsProjectFile(link))
            {
                if (TestProjectDetection.IsTestProject(link)) continue;
                string parent = Path.GetDirectoryName(link)!;
                string kind = KindForFile(link);
                string name = ProjectName(link, Path.GetFileName(parent), kind);
                projects.Add(new(name, Loc.Text("L68F3F11503E7"), LinkIdentity(link), parent, link,
                    kind, FindGitRoot(parent), new(GitState.None), LinkPath: link));
            }
            else
                projects.Add(new(Path.GetFileName(link), Loc.Text("L68F3F11503E7"), LinkIdentity(link), link, null,
                    Loc.Text("LBEAFC51072E1"), null, new(GitState.None), LinkPath: link));
        }

        string? rootKind = null;
        foreach (ScanScope scope in scopes)
        {
            if (TestProjectDetection.IsTestFolder(scope.Folder)) continue;
            if (scope.Link is null && !scope.RootOnly &&
                ProjectDetection.IsProjectMetadataFolder(new(scope.Folder), rootKind))
                continue;
            int initialCount = projects.Count;
            var pending = new Stack<FolderScan>();
            pending.Push(new(scope.Folder, scope.Link is null ? workspaceRepo : FindGitRoot(scope.Folder), false,
                ParentKind: scope.Link is null && !scope.RootOnly ? rootKind : null,
                InProjectScripts: scope.Link is null && !scope.RootOnly && rootKind is not null &&
                    Path.GetFileName(scope.Folder).Equals("scripts", StringComparison.OrdinalIgnoreCase),
                InMobileHost: scope.Link is null && !scope.RootOnly && ProjectDetection.IsMobileHost(scope.Folder, rootKind),
                OwnerFolder: scope.Link is null && !scope.RootOnly && rootKind is not null ? workspace.Root : null));
            while (pending.TryPop(out FolderScan? current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TestProjectDetection.IsTestFolder(current.Path)) continue;
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
                bool inDotNetProject = !isRepoRoot && current.InDotNetProject;
                bool inProjectScripts = !isRepoRoot && current.InProjectScripts;
                bool inMobileHost = !isRepoRoot && current.InMobileHost;
                string? parentKind = isRepoRoot ? null : current.ParentKind;
                string? ownerFolder = isRepoRoot ? null : current.OwnerFolder;
                var files = entries.OfType<FileInfo>()
                    .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .ToArray();
                var projectFiles = files.Where(file => IsProjectFile(file.FullName)).ToArray();
                if (projectFiles.Length > 0 && projectFiles.All(file => TestProjectDetection.IsTestProject(file.FullName)))
                {
                    testOnlyFolders.Add(current.Path);
                }
                files = files.Where(file => !IsProjectFile(file.FullName) || !TestProjectDetection.IsTestProject(file.FullName)).ToArray();
                var directories = entries.OfType<DirectoryInfo>().ToArray();

                FileInfo? entry = files
                    .Where(file => IsExtension(file, ".slnx", ".sln"))
                    .OrderByDescending(file => Path.GetFileNameWithoutExtension(file.Name).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(file => IsExtension(file, ".slnx"))
                    .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                string kind = "Visual Studio";
                if (ProjectDetection.DetectSpecial(current.Path, files, directories, inMobileHost ? null : parentKind) is { } special)
                    (entry, kind) = special;

                if (entry is null && !inDotNetProject)
                {
                    entry = files
                        .Where(file => IsExtension(file, ".csproj", ".vbproj", ".fsproj", ".vcxproj"))
                        .OrderByDescending(file => Path.GetFileNameWithoutExtension(file.Name).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                        .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    kind = ".NET / MSBuild";
                }
                if (entry is null)
                    (entry, kind) = ProjectDetection.Detect(current.Path, files, inMobileHost ? null : parentKind);

                string relativePath = scope.Link is null ? Path.GetRelativePath(workspace.Root, current.Path) : LinkIdentity(current.Path);
                if (entry is not null)
                {
                    bool coveredBySolution = current.ParentSolution is { } parent &&
                        IsExtension(entry, ".sln") && Path.GetFileNameWithoutExtension(entry.Name)
                            .Equals(Path.GetFileNameWithoutExtension(parent.Name), StringComparison.OrdinalIgnoreCase) &&
                        ContainsSolutionProjects(parent, entry);
                    string? role = coveredBySolution ? Loc.Text("L7BAC3214A0DD") :
                        inMobileHost && kind is ProjectDetection.Android or "Gradle" or "Xcode" ? Loc.Text("LC78FFE195710") :
                        inProjectScripts && kind == ".NET / MSBuild" ? Loc.Text("LF7586694B384") :
                        ProjectDetection.IsJavaScript(entry.FullName) && ProjectDetection.IsEmbeddedJavaScript(current.Path, ownerFolder)
                            ? Loc.Text("L7C70C099C75E") : null;
                    bool coveredByParent = role is not null && ownerFolder is not null &&
                        !current.Path.Equals(scope.Link, StringComparison.OrdinalIgnoreCase) &&
                        workspace.PreferenceFor(relativePath) == ProjectPreference.Empty &&
                        string.IsNullOrWhiteSpace(workspace.DescriptionFor(relativePath));
                    string name = ProjectName(entry.Name, folderName, kind);
                    if (!coveredByParent)
                    {
                        projects.Add(new(name, scope.Category, relativePath, current.Path, entry.FullName, kind, repo, new(GitState.None), LinkPath: scope.Link));
                        ownerFolder = current.Path;
                    }
                    else
                        components.Add(new(ownerFolder!, current.Path, name, kind, role!));
                }
                else if (isRepoRoot && !testOnlyFolders.Contains(current.Path))
                    repoFallbacks.TryAdd(current.Path, new(folderName, scope.Category, relativePath, current.Path, null, Loc.Text("LB9C67E2C19F8"), repo, new(GitState.None), LinkPath: scope.Link));

                if (kind == "Tauri" && entry is not null && directories.FirstOrDefault(child =>
                        child.Name.Equals("src-tauri", StringComparison.OrdinalIgnoreCase)) is { } tauri)
                    components.Add(new(current.Path, tauri.FullName, tauri.Name, "Tauri", Loc.Text("LC78FFE195710")));

                if (scope.RootOnly)
                {
                    rootKind = entry is null ? null : kind;
                    continue;
                }

                FileInfo? parentSolution = entry is not null && IsExtension(entry, ".slnx") ? entry : null;
                string? effectiveKind = entry is null ? parentKind : kind;
                foreach (var child in directories)
                {
                    if (!exclusions.Contains(child.Name) &&
                        !ProjectDetection.IsProjectMetadataFolder(child, effectiveKind) &&
                        (scope.Link is not null || !hiddenSubfolders.Contains(Path.GetRelativePath(workspace.Root, child.FullName))))
                        pending.Push(new(child.FullName, repo,
                            inDotNetProject || entry is not null && ProjectTools.IsDotNetEntry(entry.FullName) ||
                            kind is ".NET / MSBuild" or "Unity" or "Unreal Engine" or "Godot", parentSolution, effectiveKind,
                            inProjectScripts || entry is not null && child.Name.Equals("scripts", StringComparison.OrdinalIgnoreCase),
                            inMobileHost || entry is not null && ProjectDetection.IsMobileHost(child.FullName, kind), ownerFolder));
                }
            }
            if (scope.Link is not null && !testOnlyFolders.Contains(scope.Folder) && initialCount == projects.Count && !repoFallbacks.Values.Any(project => project.LinkPath == scope.Link))
                projects.Add(new(Path.GetFileName(scope.Folder), scope.Category, LinkIdentity(scope.Folder), scope.Folder, null,
                    Loc.Text("L153E900124CF"), FindGitRoot(scope.Folder), new(GitState.None), LinkPath: scope.Link));
        }

        var representedRepos = projects.Select(project => project.Repository).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        projects.AddRange(repoFallbacks.Where(item => !representedRepos.Contains(item.Key)).Select(item => item.Value));
        projects = projects.OrderBy(project => project.LinkPath is not null)
            .Where(project => !TestProjectDetection.IsTestFolder(project.Folder) &&
                (project.EntryFile is null || !TestProjectDetection.IsTestProject(project.EntryFile)))
            .DistinctBy(project => project.EntryFile ?? project.Folder, StringComparer.OrdinalIgnoreCase).ToList();

        projects = projects.Select(project => project with { SavedPreference = workspace.PreferenceFor(project.RelativePath) }).ToList();
        string[] repos = projects.Where(project => !project.Preference.Archived).Select(project => project.Repository)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var statuses = new ConcurrentDictionary<string, GitStatus>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(repos, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (repo, token) => statuses[repo] = await GitStatus.ReadAsync(repo, token));

        var owners = projects.ToLookup(project => project.Folder, StringComparer.OrdinalIgnoreCase);
        var componentsByFolder = components.ToLookup(component => component.Folder, StringComparer.OrdinalIgnoreCase);
        var result = projects.Select(project =>
            {
                if (string.Equals(project.LinkPath, project.Folder, StringComparison.OrdinalIgnoreCase) &&
                    componentsByFolder[project.Folder].FirstOrDefault() is { } component &&
                    owners[component.OwnerFolder].FirstOrDefault() is { } owner)
                    return project with { OwnerName = owner.Name, OwnerRelativePath = owner.RelativePath, OwnerGroupKey = owner.ProjectGroupKey };
                return project;
            })
            .Select(project => project.Preference.Archived ? project with { Repository = null } :
                project.Repository is null ? project : project with { Git = statuses[project.Repository] })
            .OrderBy(project => Array.IndexOf(folders, project.Category))
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(project => project.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(result, repos.Length, skipped, [.. layout, .. subfolders], components);
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
        ProjectDetection.IsManifest(Path.GetFileName(path));

    private static string KindForFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sln" or ".slnx" => "Visual Studio",
        _ when ProjectDetection.IsManifest(Path.GetFileName(path)) => ProjectDetection.KindForManifest(path),
        _ => ".NET / MSBuild"
    };

    private static string ProjectName(string file, string folderName, string kind) =>
        kind is "Visual Studio" or ".NET / MSBuild" ? Path.GetFileNameWithoutExtension(file) : folderName;

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
