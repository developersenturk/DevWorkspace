using System.IO;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal static class LaunchEnvironment
{
    internal static bool RelativePath(string? path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
        !path.Contains(':') && !path.Split('\\', '/').Any(p => p == "..");

    internal static string? FileFor(LaunchProfile profile, ProjectEntry project)
    {
        string? relative = profile.EnvironmentFiles?.FirstOrDefault(p => p.Key.Equals(project.RelativePath, StringComparison.OrdinalIgnoreCase)).Value;
        return relative is null ? null : Resolve(project.Folder, relative);
    }

    internal static string Resolve(string folder, string relative)
    {
        if (!RelativePath(relative)) throw new InvalidDataException(WorkbenchText.T("EnvironmentPathRule"));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string path = Path.GetFullPath(relative, root);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(WorkbenchText.T("EnvironmentPathRule"));
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(WorkbenchText.T("EnvironmentPathRule"));
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }

    internal static Dictionary<string, string> Read(string file)
    {
        if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException(WorkbenchText.T("EnvironmentFileLimit"));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int number = 0, characters = 0;
        foreach (string line in System.IO.File.ReadLines(file))
        {
            number++; characters += line.Length + 1;
            if (characters > 1024 * 1024) throw new InvalidDataException(WorkbenchText.T("EnvironmentFileLimit"));
            string text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var match = Regex.Match(text, @"^(?:export\s+)?(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*)$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new InvalidDataException(string.Format(WorkbenchText.T("EnvironmentSyntax"), number));
            string key = match.Groups["key"].Value, value = match.Groups["value"].Value.Trim();
            if (key.StartsWith("DEVWORKSPACE_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(WorkbenchText.T("EnvironmentReserved"));
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                char quote = value[0]; int end = value.LastIndexOf(quote);
                if (end == 0 || (value[(end + 1)..].Trim() is { Length: > 0 } tail && !tail.StartsWith('#')))
                    throw new InvalidDataException(string.Format(WorkbenchText.T("EnvironmentSyntax"), number));
                value = value[1..end];
                if (quote == '"') value = value.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            }
            else
            {
                int comment = value.IndexOf(" #", StringComparison.Ordinal); if (comment >= 0) value = value[..comment].TrimEnd();
            }
            if (value.Contains('\0')) throw new InvalidDataException(string.Format(WorkbenchText.T("EnvironmentSyntax"), number));
            values[key] = value;
        }
        return values;
    }

    internal static LaunchProfile Variant(LaunchProfile source, string name, string environmentName, ProjectEntry selected,
        string? file, int? port, bool selectedOnly)
    {
        var steps = source.Steps.Where(s => !selectedOnly || s.Project.Equals(selected.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (port is not null)
        {
            bool Service(LaunchStep s) => s.Project.Equals(selected.RelativePath, StringComparison.OrdinalIgnoreCase) && s.Command is not null && !s.WaitForExit;
            int index = Array.FindLastIndex(steps, s => Service(s) && s.ReadyPort is not null);
            if (index < 0) index = Array.FindLastIndex(steps, Service);
            if (index < 0) throw new InvalidDataException(WorkbenchText.T("VariantNoService"));
            steps[index] = steps[index] with { ReadyPort = port };
        }
        var files = new Dictionary<string, string>(source.EnvironmentFiles ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (string key in files.Keys.Where(k => !steps.Any(s => s.Project.Equals(k, StringComparison.OrdinalIgnoreCase))).ToArray()) files.Remove(key);
        if (!string.IsNullOrWhiteSpace(file))
        {
            _ = Resolve(selected.Folder, file);
            files[selected.RelativePath] = file;
        }
        var profile = new LaunchProfile(name.Trim(), steps, environmentName.Trim(), files.Count == 0 ? null : files);
        DevelopmentWorkflow.Validate(profile); return profile;
    }
}
