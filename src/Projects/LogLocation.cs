using System.IO;
using System.Text.RegularExpressions;

namespace DevWorkspace;

internal sealed record LogLocation(string File, int Line, int Column)
{
    internal static LogLocation? Parse(string text, string folder)
    {
        // Common MSBuild/compiler and JavaScript/Python stack locations.
        foreach (string pattern in new[] {
            @"(?<file>(?:[A-Za-z]:[\\/]|\.{0,2}[\\/])?[^\r\n<>|\""()]+?\.[A-Za-z0-9]+)\((?<line>\d+)(?:,(?<column>\d+))?\)",
            @"(?<file>(?:[A-Za-z]:[\\/]|\.{0,2}[\\/])?[^\r\n<>|\""()]+?\.[A-Za-z0-9]+):(?<line>\d+)(?::(?<column>\d+))?" })
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            string candidate = match.Groups["file"].Value.Trim();
            // Stack prefixes ("at ...") are not part of the file name.
            int drive = candidate.IndexOf(":\\", StringComparison.Ordinal);
            if (drive < 0) drive = candidate.IndexOf(":/", StringComparison.Ordinal);
            if (drive > 1) candidate = candidate[(drive - 1)..];
            try
            {
                string path = Path.GetFullPath(candidate, folder);
                if (System.IO.File.Exists(path) && int.TryParse(match.Groups["line"].Value, out int line) && line > 0)
                    return new(path, line, int.TryParse(match.Groups["column"].Value, out int col) && col > 0 ? col : 1);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }
}
