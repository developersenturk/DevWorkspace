using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevWorkspace;

internal sealed record WorkspaceConfig(
    int SchemaVersion, string Name, string[] Folders, string[] ScanExclusions,
    Dictionary<string, string>? ProjectDescriptions = null,
    string[]? HiddenFolders = null, Dictionary<string, string>? FolderLabels = null,
    Dictionary<string, ProjectPreference>? ProjectPreferences = null, string[]? LinkedProjects = null,
    string[]? ExplorerSubfolders = null, SavedProjectFilter[]? SavedFilters = null);

internal sealed record SavedProjectFilter(Guid Id, string Name, string Navigation, string Query)
{
    internal string Key => $"filter:{Id:N}";
}

internal sealed record WorkspaceFolder(string Name, string Label, bool IsVisible, bool IsNew = false);
internal sealed record ProjectPreference(bool Favorite = false, string Group = "", string Label = "", ProjectCommand[]? Commands = null,
    bool Archived = false)
{
    public static readonly ProjectPreference Empty = new();
}

internal sealed record Workspace(string Root, WorkspaceConfig Config)
{
    public const string ConfigFileName = "devworkspace.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions EditJsonOptions = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string DescriptionFor(string relativePath) =>
        Config.ProjectDescriptions?.FirstOrDefault(item => SameProjectPath(item.Key, relativePath)).Value ?? "";

    public ProjectPreference PreferenceFor(string relativePath) =>
        Config.ProjectPreferences?.FirstOrDefault(item => SameProjectPath(item.Key, relativePath)).Value ?? ProjectPreference.Empty;

    public Workspace SaveProjectFilter(SavedProjectFilter filter) => EditSavedFilters(filters =>
    {
        if (filters.Any(existing => existing.Id != filter.Id && existing.Name.Equals(filter.Name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Bu adda bir filtre zaten var. Filtre listesini yenileyin.");
        return [.. filters.Where(existing => existing.Id != filter.Id), filter];
    });

    public Workspace RemoveProjectFilter(Guid id) =>
        EditSavedFilters(filters => filters.Where(filter => filter.Id != id).ToArray());

    private Workspace EditSavedFilters(Func<SavedProjectFilter[], SavedProjectFilter[]> update) => EditConfig(document =>
    {
        var filters = document["savedFilters"]?.Deserialize<SavedProjectFilter[]>(JsonOptions) ?? [];
        document["savedFilters"] = JsonSerializer.SerializeToNode(update(filters), JsonOptions);
    });

    public Workspace AddLinks(IEnumerable<string> paths)
    {
        string[] valid = paths.Select(NormalizeLink).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (valid.Any(path => Directory.Exists(path) && IsWorkspaceAncestor(path)))
            throw new IOException("Çalışma alanının kendisi veya üst klasörü proje olarak bağlanamaz.");
        return EditLinks(existing => existing.Concat(valid).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private bool IsWorkspaceAncestor(string path)
    {
        string relative = Path.GetRelativePath(path, Root);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public Workspace RemoveLink(string path) => EditLinks(existing =>
        existing.Where(item => !item.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray());

    private Workspace EditLinks(Func<string[], string[]> update) => EditConfig(document =>
        document["linkedProjects"] = JsonSerializer.SerializeToNode(update(
            document["linkedProjects"]?.Deserialize<string[]>(JsonOptions) ?? [])));

    public static string NormalizeLink(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException("Proje için tam dosya veya klasör yolu gerekli.");
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if ((!Directory.Exists(fullPath) && (!File.Exists(fullPath) || !WorkspaceScanner.IsProjectFile(fullPath))) ||
            File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"Bu yol bağlanamıyor. Bir proje klasörü veya çözüm dosyası seçin. {fullPath}");
        return fullPath;
    }

    public Workspace SavePreference(string relativePath, Func<ProjectPreference, ProjectPreference> change) =>
        SavePreferences([relativePath], change);

    public Workspace SavePreferences(IEnumerable<string> relativePaths, Func<ProjectPreference, ProjectPreference> change) =>
        EditConfig(document =>
        {
            var preferences = document["projectPreferences"] as JsonObject;
            if (preferences is null)
            {
                if (document["projectPreferences"] is not null)
                    throw new InvalidDataException("Proje tercihleri geçersiz.");
                preferences = new JsonObject();
                document["projectPreferences"] = preferences;
            }

            foreach (string relativePath in relativePaths)
            {
                string? key = preferences.Select(item => item.Key).FirstOrDefault(item => SameProjectPath(item, relativePath));
                var current = key is null ? ProjectPreference.Empty
                    : preferences[key]?.Deserialize<ProjectPreference>(JsonOptions) ?? ProjectPreference.Empty;
                ProjectPreference updated = change(current);
                if (key is not null)
                    preferences.Remove(key);
                if (updated != ProjectPreference.Empty)
                    preferences[relativePath.Replace('\\', '/')] = JsonSerializer.SerializeToNode(updated, JsonOptions);
            }
        });

    public WorkspaceFolder[] GetFolders()
    {
        var exclusions = new HashSet<string>(Config.ScanExclusions, StringComparer.OrdinalIgnoreCase) { ".git" };
        var available = new DirectoryInfo(Root).EnumerateDirectories()
            .Where(folder => (folder.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0
                && !exclusions.Contains(folder.Name))
            .ToDictionary(folder => folder.Name, StringComparer.OrdinalIgnoreCase);
        var hidden = new HashSet<string>(Config.HiddenFolders ?? [], StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(Config.FolderLabels ?? [], StringComparer.OrdinalIgnoreCase);
        return Config.Folders.Concat(available.Keys.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(available.ContainsKey).Select(name =>
            {
                string actualName = available[name].Name;
                return new WorkspaceFolder(actualName,
                    labels.TryGetValue(actualName, out string? label) && !string.IsNullOrWhiteSpace(label) ? label : actualName,
                    !hidden.Contains(actualName));
            }).ToArray();
    }

    public void ValidateNewFolder(string name)
        => ValidateNewFolder(Root, name);

    private void ValidateNewFolder(string parent, string name)
    {
        if (!IsSimpleName(name) || IsReservedDeviceName(name) ||
            Config.ScanExclusions.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            name.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Bu ad çalışma alanı klasörü için kullanılamaz.");
        }

        string path = Path.Combine(parent, name);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"Bu ad zaten kullanılıyor. {name}");
    }

    internal static string ExplorerFolderPath(string path) => path.Replace('/', '\\');

    private static bool IsExplorerFolderPath(string? path) =>
        path is not null && ExplorerFolderPath(path).Split('\\').All(part => IsSimpleName(part) && !IsReservedDeviceName(part));

    public WorkspaceFolder[] GetExplorerSubfolders()
    {
        var hidden = (Config.HiddenFolders ?? []).Select(ExplorerFolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, label) in Config.FolderLabels ?? [])
            labels[ExplorerFolderPath(path)] = label;
        return (Config.ExplorerSubfolders ?? []).Concat(labels.Keys).Concat(hidden)
            .Select(ExplorerFolderPath).Where(path => path.Contains('\\')).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsExistingExplorerSubfolder)
            .Select(path => new WorkspaceFolder(path, labels.GetValueOrDefault(path) is { Length: > 0 } label ? label : Path.GetFileName(path), !hidden.Contains(path)))
            .ToArray();
    }

    private bool IsExistingExplorerSubfolder(string path)
    {
        try { ExistingExplorerFolder(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public Workspace AddExplorerFolder(string? parent, string name) => EditConfig(document =>
    {
        var current = WithCurrentConfig(document);
        string parentPath = parent is null ? Root : current.ExistingExplorerFolder(parent);
        current.ValidateNewFolder(parentPath, name);
        string[] rootOrder = parent is null ? current.GetFolders().Select(folder => folder.Name).ToArray() : [];
        Directory.CreateDirectory(Path.Combine(parentPath, name));
        if (parent is null)
            document["folders"] = JsonSerializer.SerializeToNode(rootOrder.Append(name).ToArray());
        else
            document["explorerSubfolders"] = JsonSerializer.SerializeToNode(
                (current.Config.ExplorerSubfolders ?? []).Append(Path.Combine(parent, name)).ToArray());
    });

    public Workspace SetExplorerFolderLabel(string path, string label) => EditConfig(document =>
    {
        var current = WithCurrentConfig(document);
        current.ExistingExplorerFolder(path);
        var labels = (current.Config.FolderLabels ?? []).Where(item => !SameProjectPath(item.Key, path)).ToDictionary();
        if (!string.IsNullOrWhiteSpace(label) && label.Trim() != Path.GetFileName(path))
            labels[path] = label.Trim();
        document["folderLabels"] = JsonSerializer.SerializeToNode(labels);
    });

    public Workspace SetExplorerFolderHidden(string path, bool hidden) => EditConfig(document =>
    {
        var current = WithCurrentConfig(document);
        current.ExistingExplorerFolder(path);
        var paths = (current.Config.HiddenFolders ?? []).Where(item => !SameProjectPath(item, path));
        document["hiddenFolders"] = JsonSerializer.SerializeToNode((hidden ? paths.Append(path) : paths).ToArray());
    });

    public Workspace MoveExplorerFolder(string path, string sibling, bool before, string[] visibleSiblings) => EditConfig(document =>
    {
        var current = WithCurrentConfig(document);
        string? parent = Path.GetDirectoryName(path);
        if (!string.Equals(parent, Path.GetDirectoryName(sibling), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Yalnızca aynı konumdaki klasörlerin sırası değiştirilebilir.");
        current.ExistingExplorerFolder(path);
        current.ExistingExplorerFolder(sibling);
        bool root = string.IsNullOrEmpty(parent);
        var order = root ? current.GetFolders().Select(folder => folder.Name).ToList() :
            (current.Config.ExplorerSubfolders ?? []).Select(ExplorerFolderPath)
                .Concat(visibleSiblings).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        order.RemoveAll(item => SameProjectPath(item, path));
        int target = order.FindIndex(item => SameProjectPath(item, sibling));
        if (target < 0)
            throw new IOException("Klasör sırası değişti. Tekrar deneyin.");
        order.Insert(before ? target : target + 1, path);
        document[root ? "folders" : "explorerSubfolders"] = JsonSerializer.SerializeToNode(order);
    });

    private Workspace WithCurrentConfig(JsonObject document) => this with
    {
        Config = document.Deserialize<WorkspaceConfig>(JsonOptions) ?? throw new InvalidDataException("Çalışma alanı ayarları geçersiz.")
    };

    private string ExistingExplorerFolder(string relativePath)
    {
        if (!IsExplorerFolderPath(relativePath))
            throw new IOException("Geçersiz klasör yolu.");

        string directory = Root;
        foreach (string part in ExplorerFolderPath(relativePath).Split('\\'))
        {
            directory = Path.Combine(directory, part);
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                part.Equals(".git", StringComparison.OrdinalIgnoreCase) || Config.ScanExclusions.Contains(part, StringComparer.OrdinalIgnoreCase))
                throw new IOException("Klasör çalışma alanında bulunamadı.");
        }
        return directory;
    }

    public Workspace SaveFolders(IReadOnlyList<WorkspaceFolder> folders)
    {
        if (folders.Any(folder => !IsSimpleName(folder.Name)) ||
            folders.Select(folder => folder.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != folders.Count)
            throw new IOException("Klasör adları geçerli ve birbirinden farklı olmalı.");

        return EditConfig(document =>
        {
            var existing = GetFolders().Select(folder => folder.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            WorkspaceFolder[] pending = folders.Where(folder => folder.IsNew && !existing.Contains(folder.Name)).ToArray();
            foreach (WorkspaceFolder folder in pending)
                ValidateNewFolder(folder.Name);
            foreach (WorkspaceFolder folder in pending)
                Directory.CreateDirectory(Path.Combine(Root, folder.Name));

            document["folders"] = JsonSerializer.SerializeToNode(folders.Select(folder => folder.Name).ToArray());
            document["hiddenFolders"] = JsonSerializer.SerializeToNode(folders.Where(folder => !folder.IsVisible).Select(folder => folder.Name).ToArray());
            document["folderLabels"] = JsonSerializer.SerializeToNode(folders
                .Where(folder => !string.IsNullOrWhiteSpace(folder.Label) && folder.Label.Trim() != folder.Name)
                .ToDictionary(folder => folder.Name, folder => folder.Label.Trim()));
        });
    }

    public Workspace SaveDescriptions(IReadOnlyDictionary<string, string> updates, bool onlyMissing = false) =>
        EditConfig(document =>
        {
            var descriptions = document["projectDescriptions"] as JsonObject;
            if (descriptions is null)
            {
                if (document["projectDescriptions"] is not null)
                    throw new InvalidDataException("Proje açıklamaları geçersiz.");
                descriptions = new JsonObject();
                document["projectDescriptions"] = descriptions;
            }

            foreach (var (path, description) in updates)
            {
                string? existing = descriptions.Select(item => item.Key).FirstOrDefault(key => SameProjectPath(key, path));
                if (onlyMissing && existing is not null)
                    continue;
                if (existing is not null)
                    descriptions.Remove(existing);
                if (!string.IsNullOrWhiteSpace(description))
                    descriptions[path.Replace('\\', '/')] = description.Trim();
            }
        });

    private Workspace EditConfig(Action<JsonObject> update) => WithConfigLock(Root, configPath =>
    {
        string? original = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        var document = (original is null ? JsonSerializer.SerializeToNode(Config, JsonOptions) : JsonNode.Parse(original))
            as JsonObject ?? throw new InvalidDataException("Çalışma alanı ayarları geçersiz.");
        update(document);

        WorkspaceConfig config = document.Deserialize<WorkspaceConfig>(JsonOptions)
            ?? throw new InvalidDataException("Çalışma alanı ayarları geçersiz.");
        string temporaryPath = Path.Combine(Root, $".devworkspace-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, document.ToJsonString(EditJsonOptions) + Environment.NewLine);
            if (original is not null && File.ReadAllText(configPath) != original)
                throw new IOException("Çalışma alanı ayarları değişti. Tekrar deneyin.");
            File.Move(temporaryPath, configPath, overwrite: original is not null);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
        return this with { Config = config };
    });

    internal static string? ImportConfiguration(string root, byte[] settings, byte[]? previous)
    {
        using (var stream = new MemoryStream(settings, writable: false))
            ReadConfiguration(stream);

        return WithConfigLock<string?>(root, configPath =>
        {
            if (previous is not null && (!File.Exists(configPath) || !File.ReadAllBytes(configPath).AsSpan().SequenceEqual(previous)))
                throw new IOException("Hedef çalışma alanının ayarları değişti. İçe aktarmayı tekrar başlatın.");

            string temporaryPath = Path.Combine(root, $".devworkspace-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew))
                    stream.Write(settings);

                if (previous is null)
                {
                    File.Move(temporaryPath, configPath);
                    return null;
                }

                string backup = Path.Combine(root, $"devworkspace-before-import-{Guid.NewGuid():N}.bak");
                File.Replace(temporaryPath, configPath, backup);
                return backup;
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        });
    }

    private static T WithConfigLock<T>(string root, Func<string, T> action)
    {
        string configPath = Path.Combine(root, ConfigFileName);
        string mutexKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configPath.ToUpperInvariant())));
        using var mutex = new Mutex(false, $"Local\\DevWorkspace-{mutexKey}");
        bool locked = false;
        try
        {
            try { locked = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked)
                throw new IOException("Çalışma alanı ayarları başka bir pencerede kaydediliyor.");

            return action(configPath);
        }
        finally
        {
            if (locked)
                mutex.ReleaseMutex();
        }
    }

    private static bool SameProjectPath(string a, string b) =>
        a.Replace('\\', '/').Equals(b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static Workspace Open(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Seçilen klasör bulunamadı.");

        string configPath = Path.Combine(root, ConfigFileName);
        if (File.Exists(configPath))
            return OpenExisting(root, configPath);

        string name = Path.GetFileName(root);
        var config = new WorkspaceConfig(
            1, string.IsNullOrEmpty(name) ? root : name,
            [],
            [".git", ".vs", ".cr", ".runtime", ".artifacts", ".codex-temp", ".pgdata", "bin", "obj",
             "node_modules", "packages", "artifacts", "dist", "build", "publish", "vendor"]);

        return new(root, config);
    }

    internal void WriteConfiguration(Stream destination)
    {
        string path = Path.Combine(Root, ConfigFileName);
        if (File.Exists(path))
        {
            using var source = File.OpenRead(path);
            source.CopyTo(destination);
        }
        else
        {
            JsonSerializer.Serialize(destination, Config, JsonOptions);
        }
    }

    private static Workspace OpenExisting(string root, string configPath)
    {
        using var stream = File.OpenRead(configPath);
        return new(root, ReadConfiguration(stream));
    }

    internal static WorkspaceConfig ReadConfiguration(Stream stream)
    {
        var config = JsonSerializer.Deserialize<WorkspaceConfig>(stream, JsonOptions);
        if (config is null || config.SchemaVersion != 1 || string.IsNullOrWhiteSpace(config.Name)
            || config.Folders is null || config.ScanExclusions is null
            || config.Folders.Any(name => !IsSimpleName(name))
            || config.ScanExclusions.Any(name => !IsSimpleName(name))
            || config.Folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Folders.Length
            || config.HiddenFolders?.Any(name => !IsExplorerFolderPath(name)) == true
            || config.FolderLabels?.Keys.Any(name => !IsExplorerFolderPath(name)) == true
            || config.ExplorerSubfolders?.Any(name => !IsExplorerFolderPath(name) || !ExplorerFolderPath(name).Contains('\\')) == true
            || config.SavedFilters?.Any(filter => filter is null || filter.Id == Guid.Empty || string.IsNullOrWhiteSpace(filter.Name)
                || string.IsNullOrWhiteSpace(filter.Navigation) || filter.Navigation.StartsWith("filter:", StringComparison.Ordinal)
                || filter.Query is null) == true
            || config.SavedFilters?.Select(filter => filter.Id).Distinct().Count() != config.SavedFilters?.Length)
        {
            throw new InvalidDataException($"{ConfigFileName} geçerli bir çalışma alanı tanımı içermiyor.");
        }

        return config;
    }

    private static bool IsSimpleName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." &&
        name == name.Trim() && !name.EndsWith('.') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsReservedDeviceName(string name)
    {
        string stem = name.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && "123456789¹²³".Contains(stem[3]);
    }
}
