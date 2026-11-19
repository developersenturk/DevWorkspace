using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DevWorkspace;

internal sealed record AppReleaseNote(string Version, string Title, string Body, DateTimeOffset? Published)
{
    public string Label => "v" + Version;
    public override string ToString() => Label;
}

internal sealed class AppReleaseNotes(HttpClient client, string stateDirectory)
{
    internal static string CurrentVersion => typeof(AppReleaseNotes).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private string CachePath => Path.Combine(stateDirectory, "release-notes.json");
    private string ReadPath => Path.Combine(stateDirectory, "release-notes-read.txt");
    internal bool HasUnread
    {
        get
        {
            try { return !File.Exists(ReadPath) || File.ReadAllText(ReadPath) != CurrentVersion; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        }
    }
    internal void MarkRead()
    {
        try { Directory.CreateDirectory(stateDirectory); File.WriteAllText(ReadPath, CurrentVersion); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Reading notes must work in read-only environments. */ }
    }
    internal static AppReleaseNote Bundled()
    {
        string language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "tr";
        using var stream = typeof(AppReleaseNotes).Assembly.GetManifestResourceStream("DevWorkspace.Updates.ReleaseNotes." + language + ".md");
        if (stream is null) throw new InvalidOperationException("Bundled release notes are missing.");
        string body = new StreamReader(stream).ReadToEnd();
        return new(CurrentVersion, "DevWorkspace " + CurrentVersion, body, null);
    }
    internal IReadOnlyList<AppReleaseNote> Local()
    {
        try
        {
            return Merge(JsonSerializer.Deserialize<AppReleaseNote[]>(File.ReadAllText(CachePath)) ?? []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return Merge([]); }
    }
    internal static IReadOnlyList<AppReleaseNote> Merge(IEnumerable<AppReleaseNote> notes) =>
        notes.Where(n => n is not null && System.Version.TryParse(n.Version, out _) && n.Version != CurrentVersion)
            .Append(Bundled()).DistinctBy(n => n.Version).OrderByDescending(n => System.Version.Parse(n.Version)).Take(21).ToArray();

    internal async Task<IReadOnlyList<AppReleaseNote>> RefreshAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/developersenturk/DevWorkspace/releases?per_page=20");
        request.Headers.UserAgent.ParseAdd("DevWorkspace/" + CurrentVersion);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var releases = new List<AppReleaseNote>();
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            string version = (release.GetProperty("tag_name").GetString() ?? "").TrimStart('v');
            if (!System.Version.TryParse(version, out _)) continue;
            string title = release.GetProperty("name").GetString() ?? "DevWorkspace " + version;
            string body = release.GetProperty("body").GetString() ?? "";
            var published = release.GetProperty("published_at");
            releases.Add(new(version, title, body, published.ValueKind == JsonValueKind.String ? published.GetDateTimeOffset() : null));
        }
        var result = Merge(releases);
        try
        {
            Directory.CreateDirectory(stateDirectory);
            string temporary = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result), token);
            File.Move(temporary, CachePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* A cache failure must not hide downloaded notes. */ }
        return result;
    }
}
