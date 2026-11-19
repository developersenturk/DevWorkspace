using System.IO;

namespace DevWorkspace;

internal sealed record ProjectDocument(string Path, string Label)
{
    public bool IsMarkdown => Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        Path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    public bool CanPreview => IsMarkdown || System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".txt" or ".rst" or ".adoc"
        || System.IO.Path.GetFileName(Path).Equals("README", StringComparison.OrdinalIgnoreCase);
}

internal static class ProjectDocuments
{
    private const int MaxDocuments = 100;
    private const int MaxPreviewCharacters = 240_000;

    public static IReadOnlyList<ProjectDocument> Find(ProjectEntry project)
    {
        var documents = new List<ProjectDocument>();
        AddFromRoot(project.Folder, "", documents);
        if (project.Repository is not null && !project.Repository.Equals(project.Folder, StringComparison.OrdinalIgnoreCase))
            AddFromRoot(project.Repository, Loc.Text("L282D9622B8F9"), documents);

        return documents.OrderBy(document => !IsReadme(document.Path))
            .ThenBy(document => document.Label.StartsWith(Loc.Text("L282D9622B8F9"), StringComparison.Ordinal))
            .ThenBy(document => document.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string Read(ProjectDocument document)
    {
        using var reader = new StreamReader(document.Path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxPreviewCharacters + 1];
        int count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > MaxPreviewCharacters)
            throw new IOException(Loc.Text("L8E2B8FC9E100"));

        return new string(buffer, 0, count);
    }

    private static void AddFromRoot(string root, string prefix, List<ProjectDocument> documents)
    {
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        while (pending.TryPop(out var current) && documents.Count < MaxDocuments)
        {
            FileSystemInfo[] entries;
            try
            {
                if (current.Directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                entries = current.Directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries.OrderBy(entry => !IsReadme(entry.Name)).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (entry is FileInfo file && IsDocument(file.Name) &&
                    (current.Depth > 0 || !IsInternalInstructions(file.Name)))
                {
                    documents.Add(new(file.FullName, prefix + Path.GetRelativePath(root, file.FullName)));
                    if (documents.Count == MaxDocuments)
                        break;
                }
                if (entry is DirectoryInfo child && current.Depth < 4 &&
                    (current.Depth > 0 || child.Name.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
                     child.Name.Equals("documentation", StringComparison.OrdinalIgnoreCase)))
                {
                    pending.Push((child, current.Depth + 1));
                }
            }
        }
    }

    private static bool IsReadme(string path) =>
        Path.GetFileName(path).StartsWith("README", StringComparison.OrdinalIgnoreCase);

    private static bool IsInternalInstructions(string name) =>
        name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AI_RULES.md", StringComparison.OrdinalIgnoreCase);

    private static bool IsDocument(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".md" or ".markdown" or ".txt" or ".rst" or ".adoc" or ".pdf" or ".docx"
        || name.Equals("README", StringComparison.OrdinalIgnoreCase);
}
