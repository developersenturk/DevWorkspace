using System.IO;
using System.Text.Json;

namespace DevWorkspace;

internal sealed record WorkspaceSession(string? SelectedProject, string[] RecentProjects, string[]? ExpandedFolders = null,
    DateTime? LastUsedUtc = null, string? LastSolution = null, WorkspaceViewSession? View = null,
    WorkspaceActivity? Activity = null, LaunchProfile[]? Profiles = null, TrackedFinding[]? TrackedFindings = null,
    Dictionary<string, ResumeNote>? ResumeNotes = null, string? LastProfile = null, string[]? RequiredTools = null,
    WorkSet[]? WorkSets = null, Guid? LastWorkSet = null);
internal sealed record ResumeNote(string Context, string Next, DateTime UpdatedUtc);
internal sealed record LaunchProfile(string Name, LaunchStep[] Steps, string? EnvironmentName = null,
    Dictionary<string, string>? EnvironmentFiles = null);
internal sealed record LaunchStep(string Project, ProjectCommand? Command, string Action = "default",
    int? ReadyPort = null, bool WaitForExit = false, int TimeoutSeconds = 60);
internal sealed record TrackedFinding(string Id, string Project, string Title, string Detail, string Target, DateTimeOffset Due);
internal sealed record WorkspaceActivity(Dictionary<string, string> Git, Dictionary<string, string[]> NuGet,
    Dictionary<string, string> FailedActions);
internal sealed record WorkspaceViewSession(string Navigation, string Search, bool GitTab, string? DetailsProject,
    GitViewSession? Git, string? ActivePanel, string[]? ExpandedProjectGroups = null);
internal sealed record GitViewSession(string Project, string? File, bool Staged, bool DiffOpen, string? FetchRemote, string Draft);
internal sealed record ColumnLayout(double Value, string Unit, bool Visible = true);
internal sealed record WindowLayout(double Left, double Top, double Width, double Height, bool Maximized);
internal sealed record WorkbenchLayout(
    bool Comfortable = false, bool GroupVersions = true, double ExplorerWidth = 207, ColumnLayout[]? Columns = null,
    WindowLayout? Window = null, byte[]? Dock = null);

internal sealed class SessionState
{
    public string Language { get; set; } = "en";
    public bool OpenDiffOnDoubleClick { get; set; } = true;
    public bool WelcomeCompleted { get; set; }
    public bool UsageMetricsEnabled { get; set; }
    public Dictionary<string, long> UsageCounters { get; set; } = new();
    public Dictionary<string, string> VisualStudioPreferences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DarkTheme { get; set; }
    public string? ThemeName { get; set; }
    public string? LastWorkspace { get; set; }
    public ProjectTemplate[] Templates { get; set; } = [];
    public string? GitRecoveryFolder { get; set; }
    public Dictionary<string, WorkspaceSession> Workspaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public WorkbenchLayout? Layout { get; set; }
    public Dictionary<string, byte[]> SavedLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NuGetScanSnapshot> NuGetScans { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevWorkspace", "session.json");

    public static SessionState Load() => LoadFrom(FilePath);

    internal static SessionState LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path)) is { } state)
            {
                state.Workspaces = new(state.Workspaces ?? [], StringComparer.OrdinalIgnoreCase);
                state.SavedLayouts = new(state.SavedLayouts ?? [], StringComparer.OrdinalIgnoreCase);
                state.NuGetScans = new(state.NuGetScans ?? [], StringComparer.OrdinalIgnoreCase);
                state.Language = Loc.NormalizeLanguage(state.Language);
                // Sessions created before the language setting used the Turkish interface.
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("Language", out _)) state.Language = "tr";
                state.UsageCounters = new((state.UsageCounters ?? []).Where(pair =>
                    UsageMetrics.Events.Contains(pair.Key) && pair.Value > 0).ToDictionary());
                state.VisualStudioPreferences = new(state.VisualStudioPreferences ?? [], StringComparer.OrdinalIgnoreCase);
                return state;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        return new();
    }

    public WorkspaceSession For(string root) =>
        Workspaces.TryGetValue(root, out var session) && session is not null ? session : new(null, []);

    public void Remember(string root, string? selected, bool opened = false)
    {
        LastWorkspace = root;
        var previous = For(root);
        string[] recent = previous.RecentProjects ?? [];
        if (opened && selected is not null)
            recent = new[] { selected }.Concat(recent.Where(path => !path.Equals(selected, StringComparison.OrdinalIgnoreCase))).Take(30).ToArray();
        Workspaces[root] = previous with { SelectedProject = selected, RecentProjects = recent, LastUsedUtc = DateTime.UtcNow };
    }

    public void RememberExpandedFolders(string root, IEnumerable<string> folders) =>
        Workspaces[root] = For(root) with { ExpandedFolders = folders.Order(StringComparer.OrdinalIgnoreCase).ToArray() };

    public void RememberView(string root, WorkspaceViewSession view) =>
        Workspaces[root] = For(root) with { View = view };

    public void RememberSolution(string root, string path) =>
        Workspaces[root] = For(root) with { LastSolution = path };

    public void RememberActivity(string root, WorkspaceActivity activity) =>
        Workspaces[root] = For(root) with { Activity = activity };

    public void RememberProfiles(string root, LaunchProfile[] profiles) =>
        Workspaces[root] = For(root) with { Profiles = profiles };

    public void RememberFindings(string root, TrackedFinding[] findings) =>
        Workspaces[root] = For(root) with { TrackedFindings = findings };

    public void Save() => SaveTo(FilePath);

    internal bool SaveTo(string path)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
