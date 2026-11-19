using System.IO;

namespace DevWorkspace;

internal sealed record SearchHit(string Project, string File, int Line, string Preview);
internal sealed record SearchReport(SearchHit[] Hits, int Checked, int Skipped, bool Limited);

internal static class WorkspaceSearch
{
    internal static readonly string[] Exclusions = [".git", ".vs", "bin", "obj", "dist", "build", "coverage", .. ProjectDetection.GeneratedFolders];
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".vb", ".fs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".xaml", ".json", ".xml", ".config", ".yaml", ".yml", ".toml", ".ini", ".md", ".txt", ".js", ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".html", ".css", ".scss", ".sql", ".py", ".rs", ".go", ".java", ".kt", ".dart", ".c", ".cpp", ".h", ".ps1", ".sh", ".bat", ".cmd", ".gradle" };

    internal static SearchReport Find(IEnumerable<ProjectEntry> projects, IEnumerable<string> exclusions, string query, bool matchCase, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search query is empty.");
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int checkedFiles = 0, skipped = 0;
        foreach (var project in projects.OrderByDescending(p => p.Folder.Length))
        foreach (var file in WorkspaceTree.Files(project.Folder, exclusions.Concat(Exclusions), token, _ => skipped++))
        {
            if (!seen.Add(file.FullName)) continue;
            if (checkedFiles >= 50_000) return new(hits.ToArray(), checkedFiles, skipped, true);
            try
            {
                if (file.Length > 2_000_000 || (!TextExtensions.Contains(file.Extension) && file.Name is not "Dockerfile" and not ".gitignore")) { skipped++; continue; }
                checkedFiles++;
                using var reader = new StreamReader(file.FullName, detectEncodingFromByteOrderMarks: true);
                int lineNumber = 0;
                while (reader.ReadLine() is { } line)
                {
                    token.ThrowIfCancellationRequested();
                    lineNumber++;
                    if (line.Contains('\0')) { skipped++; break; }
                    int at = line.IndexOf(query, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;
                    int start = Math.Max(0, at - 80);
                    hits.Add(new(project.Name, file.FullName, lineNumber, line.Substring(start, Math.Min(350, line.Length - start))));
                    if (hits.Count >= 2000) return new(hits.ToArray(), checkedFiles, skipped, true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(hits.ToArray(), checkedFiles, skipped, false);
    }
}
