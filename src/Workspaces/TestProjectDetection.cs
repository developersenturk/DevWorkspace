using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DevWorkspace;

internal static class TestProjectDetection
{
    private static readonly Regex TestName = new(@"(?:^|[._-])(?:tests?|unit-?tests?|integration-?tests?|functional-?tests?|ui-?tests?|e2e-?tests?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsTestFolder(string folder)
    {
        try
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(folder)); directory is not null; directory = directory.Parent)
            {
                if (directory.Name.Equals("__tests__", StringComparison.OrdinalIgnoreCase) || TestName.IsMatch(directory.Name)) return true;
                // A separate repository below a directory called 'tests' retains its own project identity.
                string marker = Path.Combine(directory.FullName, ".git");
                if (File.Exists(marker) || Directory.Exists(marker)) break;
            }
            return false;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    internal static bool IsTestProject(string path)
    {
        if (IsTestFolder(Path.GetDirectoryName(Path.GetFullPath(path))!) || TestName.IsMatch(Path.GetFileNameWithoutExtension(path))) return true;
        if (Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx")
        {
            try
            {
                var (references, properties) = ProjectMovePlanner.ReadReferences(new(path));
                var projects = references.Select(reference => ProjectMovePlanner.Resolve(reference, path, properties)).OfType<string>()
                    .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".csproj" or ".vbproj" or ".fsproj" or ".vcxproj").ToArray();
                return projects.Length > 0 && projects.All(IsTestProject);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException) { return false; }
        }
        if (Path.GetExtension(path).ToLowerInvariant() is not (".csproj" or ".vbproj" or ".fsproj" or ".vcxproj")) return false;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_500_000 });
            var document = XDocument.Load(reader);
            if (document.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))) return true;
            return document.Descendants().Where(e => e.Name.LocalName == "PackageReference").Any(e =>
            {
                string package = ((string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "").Trim();
                return package.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
                    package.Equals("xunit", StringComparison.OrdinalIgnoreCase) || package.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase) ||
                    package.Equals("NUnit", StringComparison.OrdinalIgnoreCase) || package.Equals("MSTest.TestFramework", StringComparison.OrdinalIgnoreCase) ||
                    package.Equals("TUnit", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException) { return false; }
    }
}
