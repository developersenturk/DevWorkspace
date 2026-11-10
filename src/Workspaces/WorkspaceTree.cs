using System.IO;

namespace DevWorkspace;

internal static class WorkspaceTree
{
    public static IEnumerable<FileInfo> Files(string root, IEnumerable<string> exclusions, CancellationToken token, Action<string> skipped)
    {
        var ignored = new HashSet<string>(exclusions, StringComparer.OrdinalIgnoreCase) { ".git" };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new(root));
        while (pending.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try
            {
                if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped(folder.FullName);
                    continue;
                }
                entries = folder.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped(folder.FullName);
                continue;
            }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (!ignored.Contains(entry.Name))
                        skipped(entry.FullName);
                    continue;
                }
                if (entry is DirectoryInfo child && !ignored.Contains(child.Name))
                    pending.Push(child);
                else if (entry is FileInfo file && !ignored.Contains(file.Name))
                    yield return file;
            }
        }
    }
}
