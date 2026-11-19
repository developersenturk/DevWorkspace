using System.Diagnostics;
using System.IO;
using System.Text;

namespace DevWorkspace;

internal static class GitFilePreview
{
    private const int Limit = 64_000;

    public static async Task<GitDiffPreview> ReadAsync(string repository, GitFileChange change, bool staged, CancellationToken cancellationToken)
    {
        if (change.Index == '?')
            return await ReadUntrackedAsync(repository, change.Path, cancellationToken);

        try
        {
            var (patch, truncated) = await ReadPatchAsync(repository, change, staged, "1000000", cancellationToken);
            if (!truncated)
                return GitDiffPreview.FromPatch(patch);

            (patch, truncated) = await ReadPatchAsync(repository, change, staged, "4", cancellationToken);
            if (truncated && !patch.EndsWith('\n'))
                patch = patch[..(patch.LastIndexOf('\n') + 1)];
            return GitDiffPreview.FromPatch(patch, truncated
                ? Loc.Text("LD55A20BEFFDA")
                : Loc.Text("L2A071913F9E6"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], Loc.Text("LB46B08E05BF9"));
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadPatchAsync(
        string repository, GitFileChange change, bool staged, string context, CancellationToken token)
    {
        var arguments = new List<string> { "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "diff.suppressBlankEmpty=false",
            "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--word-diff=none", $"--unified={context}",
            "--output-indicator-new=+", "--output-indicator-old=-", "--output-indicator-context= " };
        if (staged)
            arguments.Add("--cached");
        else if (change.IsConflict)
            arguments.Add("--ours");
        arguments.Add("--");
        arguments.Add(change.Path);
        if (staged && change.OriginalPath is not null)
            arguments.Add(change.OriginalPath);

        token.ThrowIfCancellationRequested();
        using var process = Process.Start(GitProjectOperations.CreateStartInfo(repository, arguments))
            ?? throw new IOException(Loc.Text("L1E302889B87D"));
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var cancel = timeout.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = CaptureAsync(process.StandardOutput, timeout.Token);
        var error = CaptureAsync(process.StandardError, timeout.Token);
        await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
        timeout.Token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new IOException((await error).Text.Trim());
        return await output;
    }

    private static async Task<(string Text, bool Truncated)> CaptureAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            int available = Math.Min(count, Limit - result.Length);
            if (available > 0)
                result.Append(buffer, 0, available);
            truncated |= available < count;
        }
        return (result.ToString(), truncated);
    }

    private static async Task<GitDiffPreview> ReadUntrackedAsync(string repository, string relative, CancellationToken token)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        string file = Path.GetFullPath(relative, root);
        string check = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(check) || check == ".." || check.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException(Loc.Text("L2AF657F5C271"));

        for (string? item = file; item is not null && !item.Equals(root, StringComparison.OrdinalIgnoreCase); item = Path.GetDirectoryName(item))
            if (File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(Loc.Text("L9C5D4E2018C7"));

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Limit + 1];
        int count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
        string text = new(buffer, 0, Math.Min(count, Limit));
        if (text.Contains('\0'))
            return new([], Loc.Text("LB074B1250292"));

        using var lines = new StringReader(text);
        var rows = new List<GitDiffRow>();
        while (lines.ReadLine() is string line)
            rows.Add(new(null, new(rows.Count + 1, line, GitDiffTone.Added)));
        return new(rows, count > Limit ? Loc.Text("LD55A20BEFFDA") : rows.Count == 0 ? Loc.Text("L36C8CD08C676") : "");
    }
}
