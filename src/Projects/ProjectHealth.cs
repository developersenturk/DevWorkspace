using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DevWorkspace;

internal sealed record HealthFinding(string Severity, string Description, string? File = null);
internal sealed record HealthResult(IReadOnlyList<HealthFinding> Findings, int FilesChecked, int Unresolved, int Skipped);

internal static class ProjectHealth
{
    private static readonly Regex Framework = new(@"^net(?<major>[5-9]|[1-9][0-9]+)\.[0-9]+", RegexOptions.IgnoreCase);

    public static async Task<HealthResult> CheckAsync(Workspace workspace, ProjectEntry project, CancellationToken token, IReadOnlyDictionary<string, string>? environment = null)
    {
        var findings = new List<HealthFinding>();
        if (project.Kind == Loc.Text("LBEAFC51072E1") || !Directory.Exists(project.Folder))
        {
            findings.Add(new(Loc.Text("LE8AF1BA5B6FF"), Loc.Text("L79A84F50AA6B")));
            return new(findings, 0, 0, 0);
        }
        if (project.EntryFile is string entry && !File.Exists(entry))
            findings.Add(new(Loc.Text("LE8AF1BA5B6FF"), Loc.Text("LED6917660D95")));

        int checkedFiles = 0, unresolved = 0, skipped = 0, targetMajor = 0;
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Queue<FileInfo>(WorkspaceTree.Files(project.Folder, workspace.Config.ScanExclusions, token, _ => skipped++)
            .Where(file => ProjectMovePlanner.IsConfiguration(file.Extension)).Take(2001));
        while (files.TryDequeue(out FileInfo? file))
        {
            token.ThrowIfCancellationRequested();
            if (!checkedPaths.Add(file.FullName))
                continue;
            if (checkedFiles >= 2000)
            {
                skipped += files.Count + 1;
                break;
            }
            try
            {
                var (references, properties) = ProjectMovePlanner.ReadReferences(file);
                checkedFiles++;
                foreach (string reference in references.Distinct(StringComparer.Ordinal))
                {
                    string? path = ProjectMovePlanner.Resolve(reference, file.FullName, properties);
                    if (path is null)
                    {
                        unresolved++;
                        continue;
                    }
                    if (!File.Exists(path) && !Directory.Exists(path))
                    {
                        string severity = Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? Loc.Text("L2EF74D2207B7") : Loc.Text("LE8AF1BA5B6FF");
                        findings.Add(new(severity, Loc.Format("L5D977FC5F032", path), file.FullName));
                    }
                }
                foreach (string name in new[] { "TargetFramework", "TargetFrameworks" })
                {
                    foreach (string target in (properties.GetValueOrDefault(name) ?? "").Split(';', StringSplitOptions.TrimEntries))
                    {
                        var match = Framework.Match(target);
                        if (match.Success && int.TryParse(match.Groups["major"].Value, out int major))
                            targetMajor = Math.Max(targetMajor, major);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                findings.Add(new(Loc.Text("LEDD1397983CA"), Loc.Text("LD09F10FCC74F"), file.FullName));
                skipped++;
            }
        }

        if (project.EntryFile is string projectFile && ProjectTools.IsDotNetEntry(projectFile) &&
            (targetMajor > 0 || HasGlobalJson(project.Folder)))
        {
            var output = new StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                int code = await ProjectTools.RunAsync(new("SDK", "dotnet.exe", "--version"), project.Folder,
                    message => { lock (output) if (output.Length < 3000) output.Append(message); }, timeout.Token, environment: environment);
                string selected = output.ToString().Trim();
                if (code != 0)
                    findings.Add(new("SDK", Loc.Text("L2A32E0CF7DC2"), FindGlobalJson(project.Folder)));
                else if (targetMajor > 0 && int.TryParse(selected.Split('.')[0], out int sdkMajor) && sdkMajor < targetMajor)
                    findings.Add(new("SDK", Loc.Format("L415B4936EA60", selected, targetMajor), projectFile));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                findings.Add(new("SDK", Loc.Text("L819D2FA28BCD")));
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            {
                findings.Add(new("SDK", Loc.Text("L62BA7AC906F6")));
            }
        }

        return new(findings, checkedFiles, unresolved, skipped);
    }

    private static bool HasGlobalJson(string folder) => FindGlobalJson(folder) is not null;

    private static string? FindGlobalJson(string folder)
    {
        for (string? current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            string path = Path.Combine(current, "global.json");
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
