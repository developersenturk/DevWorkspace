using System.Globalization;

namespace DevWorkspace;

internal enum GitState { None, Clean, Changed, Ownership, Unavailable }

internal sealed record GitFileChange(string Path, char Index, char WorkingTree, string? OriginalPath = null)
{
    public bool IsConflict => Index == 'U' || WorkingTree == 'U';
    public bool IsStaged => Index is not ('.' or '?') && !IsConflict;
    public bool HasWorkingChange => WorkingTree != '.';
    public string StatusCode => IsConflict ? "U" : (HasWorkingChange ? WorkingTree : Index).ToString();
    public string DisplayPath => OriginalPath is null ? Path : $"{OriginalPath} → {Path}";
    public string Label => Index == '?' ? Loc.Text("L7BF1133CCA55") : Index == 'U' || WorkingTree == 'U' ? Loc.Text("LEF179F25624C")
        : Index == '.' ? Describe(WorkingTree) : WorkingTree == '.' ? Loc.Format("LA717C0C0D31D", Describe(Index))
        : Loc.Format("L91891FA6CA92", Describe(Index), Describe(WorkingTree));

    private static string Describe(char status) => status switch
    {
        'A' => Loc.Text("L8D30BFE1B823"), 'M' => Loc.Text("LC2FC0AECA605"), 'D' => Loc.Text("L216E07EEBE8C"), 'R' => Loc.Text("LDCF108D7DA68"),
        'C' => Loc.Text("L02AF74B2F0D6"), 'T' => Loc.Text("L792215076BC4"), _ => Loc.Text("LC2FC0AECA605")
    };
}

internal sealed record GitStatus(
    GitState State, string Branch = "", int? Changes = null, int? Ahead = null, int? Behind = null, string Detail = "",
    IReadOnlyList<GitFileChange>? Files = null, string? HeadRef = null, string? CommitId = null,
    string? Upstream = null, IReadOnlyList<string>? Remotes = null)
{
    public string Label => State switch
    {
        GitState.Clean => Loc.Text("LF811F51EB52C"),
        GitState.Changed => Loc.Text("L10E0D7B9E625"),
        GitState.Ownership => Loc.Text("L074324369F95"),
        GitState.Unavailable => Loc.Text("LEDD1397983CA"),
        _ => Loc.Text("L4A494445A8AA")
    };

    public string Tracking => Ahead.HasValue ? $"↑ {Ahead}   ↓ {Behind}" : "—";
    public string RemoteNames => Remotes is { Count: > 0 } ? string.Join(", ", Remotes) : "—";
    public string RemoteSummary => Remotes is { Count: > 1 } ? RemoteNames : Upstream ?? RemoteNames;

    public bool HasSameSnapshot(GitStatus other) =>
        this with { Files = other.Files, Remotes = other.Remotes } == other &&
        (Files ?? []).SequenceEqual(other.Files ?? []) &&
        (Remotes ?? []).SequenceEqual(other.Remotes ?? []);

    public static async Task<GitStatus> ReadAsync(string root, CancellationToken cancellationToken)
    {
        string message;
        try
        {
            var (code, output, error) = await GitProjectOperations.RunAsync(root,
                ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
                 "status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"], cancellationToken);
            if (code == 0)
            {
                var (remoteCode, remoteOutput, _) = await GitProjectOperations.RunAsync(root, ["remote"], cancellationToken);
                return Parse(output) with
                {
                    Remotes = remoteCode == 0 ? remoteOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : null
                };
            }

            message = error.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(GitState.Unavailable, Detail: Loc.Text("LEAF4AA46FEB9"));
        }
        catch (System.IO.IOException ex)
        {
            return new(GitState.Unavailable, Detail: ex.Message);
        }

        if (message.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
            return new(GitState.Ownership, Detail: Loc.Text("L80A69AEF8A24"));

        return new(GitState.Unavailable, Detail: string.IsNullOrEmpty(message) ? Loc.Text("L9AD0B824CC75") : message);
    }

    private static GitStatus Parse(string output)
    {
        string branch = "";
        string commit = "";
        string? upstream = null;
        var changes = new List<GitFileChange>();
        int? ahead = null;
        int? behind = null;
        string[] records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < records.Length; i++)
        {
            string line = records[i];
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                branch = line["# branch.head ".Length..];
            else if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
                commit = line["# branch.oid ".Length..];
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                upstream = line["# branch.upstream ".Length..];
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 &&
                    int.TryParse(parts[2].TrimStart('+'), CultureInfo.InvariantCulture, out int a) &&
                    int.TryParse(parts[3].TrimStart('-'), CultureInfo.InvariantCulture, out int b))
                {
                    ahead = a;
                    behind = b;
                }
            }
            else if (line.StartsWith("? ", StringComparison.Ordinal))
                changes.Add(new(line[2..], '?', '?'));
            else if (line[0] is '1' or '2' or 'u')
            {
                int columns = line[0] switch { '1' => 9, '2' => 10, _ => 11 };
                string[] parts = line.Split(' ', columns);
                string? original = line[0] == '2' && i + 1 < records.Length ? records[++i] : null;
                if (parts.Length == columns && parts[1].Length == 2)
                    changes.Add(line[0] == 'u' ? new(parts[^1], 'U', 'U')
                        : new(parts[^1], parts[1][0], parts[1][1], original));
            }
        }

        string? headRef = branch is "" or "(detached)" ? null : "refs/heads/" + branch;
        if (branch == "(detached)")
            branch = $"HEAD {commit[..Math.Min(7, commit.Length)]}";

        string detail = commit == "(initial)" ? Loc.Text("L5C1B5A61A33B") : "";
        return new(changes.Count == 0 ? GitState.Clean : GitState.Changed, branch, changes.Count, ahead, behind, detail, changes,
            headRef, commit is "" or "(initial)" ? null : commit, upstream);
    }
}
