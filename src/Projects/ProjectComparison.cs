using System.IO;
using System.Text;

namespace DevWorkspace;

internal enum FileDifferenceKind { Modified, LeftOnly, RightOnly, Unreadable }

internal sealed record FileDifference(string Path, string? LeftPath, string? RightPath, FileDifferenceKind Kind)
{
    public string Label => Kind switch
    {
        FileDifferenceKind.LeftOnly => Loc.Text("LDBC37A445165"),
        FileDifferenceKind.RightOnly => Loc.Text("L801CA4BAD200"),
        FileDifferenceKind.Unreadable => Loc.Text("LEDD1397983CA"),
        _ => Loc.Text("L15328D0AEF91")
    };
}

internal sealed record ComparisonResult(IReadOnlyList<FileDifference> Differences, int Identical, int Skipped);
internal sealed record DiffLine(int? LeftNumber, string Left, int? RightNumber, string Right, bool Changed)
{
    public bool Removed => Changed && LeftNumber.HasValue;
    public bool Added => Changed && RightNumber.HasValue;
}
internal sealed record DiffPreview(IReadOnlyList<DiffLine> Lines, string Notice = "");

internal static class ProjectComparison
{
    private const int PreviewLimit = 350_000;
    private const int AlignmentLimit = 2_000_000;

    public static ComparisonResult Compare(string left, string right, IEnumerable<string> exclusions, CancellationToken token)
    {
        int skipped = 0;
        Dictionary<string, FileInfo> Collect(string root)
        {
            var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo file in WorkspaceTree.Files(root, exclusions, token, _ => skipped++))
                if (!files.TryAdd(Path.GetRelativePath(root, file.FullName), file))
                    skipped++;
            return files;
        }

        var a = Collect(left);
        var b = Collect(right);
        var differences = new List<FileDifference>();
        var leftBuffer = new byte[65536];
        var rightBuffer = new byte[65536];
        int identical = 0;
        foreach (string path in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            a.TryGetValue(path, out var leftFile);
            b.TryGetValue(path, out var rightFile);
            if (leftFile is null || rightFile is null)
                differences.Add(new(path, leftFile?.FullName, rightFile?.FullName,
                    leftFile is null ? FileDifferenceKind.RightOnly : FileDifferenceKind.LeftOnly));
            else
            {
                try
                {
                    if (SameContent(leftFile, rightFile, leftBuffer, rightBuffer, token))
                        identical++;
                    else
                        differences.Add(new(path, leftFile.FullName, rightFile.FullName, FileDifferenceKind.Modified));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    differences.Add(new(path, leftFile.FullName, rightFile.FullName, FileDifferenceKind.Unreadable));
                }
            }
        }
        return new(differences, identical, skipped);
    }

    private static bool SameContent(FileInfo left, FileInfo right, byte[] a, byte[] b, CancellationToken token)
    {
        if (left.Length != right.Length)
            return false;

        using var leftFile = left.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var rightFile = right.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = leftFile.ReadAtLeast(a, a.Length, false);
            int other = rightFile.ReadAtLeast(b, b.Length, false);
            if (count != other || !a.AsSpan(0, count).SequenceEqual(b.AsSpan(0, other)))
                return false;
            if (count == 0)
                return true;
        }
    }

    public static DiffPreview Preview(FileDifference difference, CancellationToken token)
    {
        try
        {
            string[] left = ReadLines(difference.LeftPath);
            string[] right = ReadLines(difference.RightPath);
            if (left.Length > 6000 || right.Length > 6000)
                return new([], Loc.Text("L28E6CBFAB9A4"));
            if ((long)left.Length * right.Length > AlignmentLimit)
                return new(Pair(left, right, 0, 0).ToArray(), Loc.Text("LF17E8DCF603E"));

            var scores = new int[left.Length + 1, right.Length + 1];
            for (int i = left.Length - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                for (int j = right.Length - 1; j >= 0; j--)
                    scores[i, j] = left[i] == right[j] ? scores[i + 1, j + 1] + 1 : Math.Max(scores[i + 1, j], scores[i, j + 1]);
            }

            var rows = new List<DiffLine>();
            int x = 0, y = 0, leftStart = 0, rightStart = 0;
            void Flush() => rows.AddRange(Pair(left[leftStart..x], right[rightStart..y], leftStart, rightStart));
            while (x < left.Length && y < right.Length)
            {
                token.ThrowIfCancellationRequested();
                if (left[x] == right[y])
                {
                    Flush();
                    rows.Add(new(x + 1, left[x], y + 1, right[y], false));
                    leftStart = ++x;
                    rightStart = ++y;
                }
                else if (scores[x + 1, y] >= scores[x, y + 1])
                    x++;
                else
                    y++;
            }

            x = left.Length;
            y = right.Length;
            Flush();
            return new(rows);
        }
        catch (DecoderFallbackException)
        {
            return new([], Loc.Text("L14FE45FDCD02"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new([], ex.Message);
        }
    }

    private static IEnumerable<DiffLine> Pair(string[] left, string[] right, int leftStart, int rightStart)
    {
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            bool a = i < left.Length, b = i < right.Length;
            yield return new(a ? leftStart + i + 1 : null, a ? left[i] : "", b ? rightStart + i + 1 : null,
                b ? right[i] : "", !a || !b || left[i] != right[i]);
        }
    }

    private static string[] ReadLines(string? path)
    {
        if (path is null)
            return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > PreviewLimit)
            throw new IOException(Loc.Text("LF1B540820D7B"));

        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[PreviewLimit + 1];
        int count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > PreviewLimit)
            throw new IOException(Loc.Text("LF1B540820D7B"));
        var text = new string(buffer, 0, count);
        if (text.Contains('\0'))
            throw new IOException(Loc.Text("LB074B1250292"));
        return text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');
    }
}
