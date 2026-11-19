using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal enum GitDiffTone { Context, Added, Removed, Missing, Gap }

internal sealed record GitDiffLine(int? Number, string Text, GitDiffTone Tone)
{
    public string Marker => Tone switch
    {
        GitDiffTone.Added => "+",
        GitDiffTone.Removed => "−",
        _ => ""
    };
}

internal sealed record GitDiffRow(GitDiffLine? Before, GitDiffLine? After)
{
    private static readonly GitDiffLine Missing = new(null, "", GitDiffTone.Missing);
    public GitDiffLine BeforeCell => Before ?? Missing;
    public GitDiffLine AfterCell => After ?? Missing;
    public string BeforeText => BeforeCell.Text;
    public string AfterText => AfterCell.Text;
}

internal sealed record GitDiffPreview(IReadOnlyList<GitDiffRow> Rows, string Message)
{
    private static readonly Regex Hunk = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@",
        RegexOptions.CultureInvariant);

    public static GitDiffPreview FromPatch(string patch, string message = "")
    {
        var rows = new List<GitDiffRow>();
        var removed = new List<GitDiffLine>();
        var added = new List<GitDiffLine>();
        int before = 1, after = 1;
        bool inHunk = false, binary = false, noFinalNewline = false;
        void FlushChanges()
        {
            for (int i = 0; i < Math.Max(removed.Count, added.Count); i++)
                rows.Add(new(i < removed.Count ? removed[i] : null, i < added.Count ? added[i] : null));
            removed.Clear();
            added.Clear();
        }

        using var reader = new StringReader(patch);
        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("diff --", StringComparison.Ordinal))
            {
                FlushChanges();
                before = after = 1;
                inHunk = false;
            }
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                FlushChanges();
                Match hunk = Hunk.Match(line);
                if (!hunk.Success)
                    continue;

                int oldStart = int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture);
                int newStart = int.Parse(hunk.Groups[2].Value, CultureInfo.InvariantCulture);
                if (oldStart > before || newStart > after)
                {
                    var gap = new GitDiffLine(null, Loc.Text("L635ACE2C1EAB"), GitDiffTone.Gap);
                    rows.Add(new(gap, gap));
                }
                before = oldStart;
                after = newStart;
                inHunk = true;
            }
            else if (!inHunk)
            {
                binary |= line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                    line == "GIT binary patch";
            }
            else if (line.Length > 0)
            {
                switch (line[0])
                {
                    case '-':
                        removed.Add(new(before++, line[1..], GitDiffTone.Removed));
                        break;
                    case '+':
                        added.Add(new(after++, line[1..], GitDiffTone.Added));
                        break;
                    case ' ':
                        FlushChanges();
                        rows.Add(new(new(before++, line[1..], GitDiffTone.Context),
                            new(after++, line[1..], GitDiffTone.Context)));
                        break;
                    case '\\':
                        noFinalNewline = true;
                        break;
                }
            }
        }
        FlushChanges();

        if (message.Length == 0)
            message = rows.Count == 0 ? (binary ? Loc.Text("LCEDA091946C8") : Loc.Text("L66EEE780E3A5")) :
                noFinalNewline ? Loc.Text("L851A81D4C139") : "";
        return new(rows, message);
    }
}
