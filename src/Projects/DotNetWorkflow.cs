using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed record DotNetWorkflowPlan(string Path, string Target, string Content);

internal static class DotNetWorkflow
{
    private const string FileName = "devworkspace-dotnet-ci.yml";
    private static readonly Regex FrameworkVersion = new(@"\Anet(?<version>[0-9]+\.[0-9]+)(?:-|\z)", RegexOptions.IgnoreCase);
    private static readonly Regex SdkVersion = new(@"\A(?<channel>[0-9]+\.[0-9]+)\.[0-9]+(?:-[0-9A-Za-z.-]+)?\z");

    internal static bool Supports(ProjectEntry project) =>
        project.Repository is not null && project.EntryFile is { } file && ProjectTools.IsDotNetEntry(file);

    internal static string? ExistingWorkflow(ProjectEntry project)
    {
        string folder = WorkflowDirectory(project.Repository!);
        if (!Directory.Exists(folder))
            return null;

        string generated = Path.Combine(folder, FileName);
        return File.Exists(generated) ? generated : Directory.EnumerateFiles(folder)
            .FirstOrDefault(file => Path.GetExtension(file).ToLowerInvariant() is ".yml" or ".yaml");
    }

    internal static DotNetWorkflowPlan Prepare(ProjectEntry project, CancellationToken token)
    {
        if (!Supports(project) || project.EntryFile is not { } entry || !File.Exists(entry))
            throw new IOException(Loc.Text("L4412A78D371E"));
        string repo = project.Repository!;
        if (!Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(Path.Combine(repo, ".git")))
            throw new IOException(Loc.Text("L0F1A95FD3699"));
        if (ExistingWorkflow(project) is not null)
            throw new IOException(Loc.Text("L135D3E4C9A80"));

        string target = RelativePath(repo, entry);
        string workingDirectory = Path.GetDirectoryName(entry)!;
        string workdir = RelativePath(repo, workingDirectory);
        var (globalFile, pinnedChannel, mtp) = ReadSdkSettings(repo, workingDirectory, token);
        var channels = FindChannels(repo, entry, token);
        if (pinnedChannel is not null)
            channels.Remove(pinnedChannel);
        else if (channels.Count == 0 || mtp || Path.GetExtension(entry).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            channels.Add("10.0");

        var setup = new List<string>();
        if (globalFile is not null)
            setup.Add($"          global-json-file: {Quote(globalFile)}");
        if (channels.Count > 0)
        {
            setup.Add("          dotnet-version: |");
            setup.AddRange(channels.OrderBy(Version.Parse).Select(version => $"            {version}.x"));
        }
        bool solution = Path.GetExtension(entry).ToLowerInvariant() is ".sln" or ".slnx";
        string testTarget = mtp ? solution ? "--solution " : "--project " : "";
        string content = $$"""
            name: .NET build and test

            on:
              push:
                branches: ['**']

            permissions:
              contents: read

            jobs:
              build-and-test:
                runs-on: windows-latest
                timeout-minutes: 30
                defaults:
                  run:
                    shell: pwsh
                    working-directory: {{Quote(workdir)}}
                env:
                  DEVWORKSPACE_TARGET: {{Quote("./" + Path.GetFileName(entry))}}
                steps:
                  - name: Check out source
                    uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7
                    with:
                      persist-credentials: false
                  - name: Set up .NET
                    uses: actions/setup-dotnet@0f4a354dd5547a28c70b7853dbd95d5b67886e73 # v6
                    with:
            {{string.Join("\n", setup)}}
                  - name: Restore dependencies
                    run: dotnet restore "$env:DEVWORKSPACE_TARGET" -p:Configuration=Release
                  - name: Build
                    run: dotnet build "$env:DEVWORKSPACE_TARGET" --configuration Release --no-restore
                  - name: Test
                    run: dotnet test {{testTarget}}"$env:DEVWORKSPACE_TARGET" --configuration Release --no-build
            """;
        return new(Path.Combine(WorkflowDirectory(repo), FileName), target, content.ReplaceLineEndings("\n") + "\n");
    }

    internal static void Create(ProjectEntry project, DotNetWorkflowPlan plan, CancellationToken token)
    {
        if (Prepare(project, token) != plan)
            throw new IOException(Loc.Text("L4344DEB9EB90"));

        string folder = WorkflowDirectory(project.Repository!);
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, $".devworkspace-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, plan.Content);
            token.ThrowIfCancellationRequested();
            if (ExistingWorkflow(project) is not null)
                throw new IOException(Loc.Text("L135D3E4C9A80"));
            File.Move(temporary, plan.Path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static HashSet<string> FindChannels(string repo, string entry, CancellationToken token)
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(entry);
        while (pending.TryPop(out string? path))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(path))
                continue;
            var (references, properties) = ProjectMovePlanner.ReadReferences(new(path));
            if (properties.GetValueOrDefault("TargetFrameworkVersion") is { Length: > 0 } &&
                !properties.ContainsKey("TargetFramework") && !properties.ContainsKey("TargetFrameworks"))
                throw new IOException(Loc.Format("L701EA8751EF1", RelativePath(repo, path)));
            foreach (string property in new[] { "TargetFramework", "TargetFrameworks" })
                foreach (string target in (properties.GetValueOrDefault(property) ?? "").Split(';', StringSplitOptions.TrimEntries))
                    if (FrameworkVersion.Match(target) is { Success: true } match)
                        channels.Add(match.Groups["version"].Value);

            foreach (string reference in references)
            {
                if (!ProjectTools.IsDotNetEntry(reference) || ProjectMovePlanner.Resolve(reference, path, properties) is not { } resolved)
                    continue;
                RelativePath(repo, resolved);
                if (!File.Exists(resolved))
                    throw new IOException(Loc.Format("L2781A1240CE7", reference));
                pending.Push(resolved);
            }
        }
        return channels;
    }

    private static (string? File, string? Channel, bool Mtp) ReadSdkSettings(string repo, string folder, CancellationToken token)
    {
        for (string? current = folder; current is not null; current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(current, "global.json");
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                using var json = JsonDocument.Parse(stream, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
                });
                var root = json.RootElement;
                bool mtp = root.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object &&
                    test.TryGetProperty("runner", out var runner) && runner.GetString() == Loc.Text("L5AEFE3A033AA");
                if (!root.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object ||
                    !sdk.TryGetProperty("version", out var version))
                    return (null, null, mtp);
                var match = SdkVersion.Match(version.GetString() ?? "");
                if (!match.Success)
                    throw new IOException(Loc.Text("LC622808B31FA"));
                return (RelativePath(repo, path), match.Groups["channel"].Value, mtp);
            }
            if (current.Equals(repo, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return (null, null, false);
    }

    private static string WorkflowDirectory(string repo)
    {
        string github = Path.Combine(repo, ".github");
        string workflows = Path.Combine(github, "workflows");
        foreach (string path in new[] { repo, github, workflows })
        {
            if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(Loc.Text("L2A4B21F1C62A"));
        }
        return workflows;
    }

    private static string RelativePath(string repo, string path)
    {
        string relative = Path.GetRelativePath(repo, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new IOException(Loc.Format("L84D3F1A9EDA4", path));
        return relative;
    }

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl) || value.Contains("${{", StringComparison.Ordinal))
            throw new IOException(Loc.Text("L62CFBFE5E19A"));
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
