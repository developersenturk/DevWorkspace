using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DevWorkspace;

internal sealed record MoveFinding(string Severity, string File, string Reference, string Detail);
internal sealed record MovePlan(string Destination, int FilesChecked, IReadOnlyList<MoveFinding> Findings, int Unresolved, int Skipped);

internal static class ProjectMovePlanner
{
    private static readonly Regex Property = new(@"\$\((?<name>[^)]+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex SlnProject = new(@"^Project\(.*?\)\s*=\s*""[^""]*"",\s*""(?<path>[^""]+)""", RegexOptions.CultureInvariant);

    public static MovePlan Analyze(Workspace workspace, ProjectEntry project, string destinationParent, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(destinationParent))
            throw new IOException("Hedef klasörün tam yolunu yazın.");

        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Folder));
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationParent));
        string target = Path.Combine(parent, Path.GetFileName(source));
        if (!Inside(source, workspace.Root) || Same(source, workspace.Root) || !Directory.Exists(source))
            throw new IOException("Kaynak proje çalışma alanı içinde bulunamadı.");
        if (!Inside(parent, workspace.Root) || !Directory.Exists(parent) || Inside(parent, source))
            throw new IOException("Hedef, çalışma alanındaki başka bir mevcut klasör olmalı.");

        AssertPlainPath(source, workspace.Root);
        AssertPlainPath(parent, workspace.Root);
        if (Same(source, target))
            throw new IOException("Proje zaten bu klasörün altında.");

        var findings = new List<MoveFinding>();
        if (Directory.Exists(target) || File.Exists(target))
            findings.Add(new("Çakışma", target, "", "Hedefte aynı adlı bir dosya veya klasör var."));
        if (project.Repository is not null && !Same(project.Repository, source))
            findings.Add(new("Git uyarısı", project.Repository, project.RelativePath, "Proje bir deponun alt klasörü. Konum değişikliği Git tarafından silme ve ekleme olarak görülebilir."));
        if (File.Exists(Path.Combine(source, ".git")))
            findings.Add(new("Git uyarısı", source, ".git", "Git çalışma ağacı veya alt modül bağlantısı ayrıca kontrol edilmeli."));

        int checkedFiles = 0, unresolved = 0, skipped = 0;
        string sourceName = Path.GetFileName(source);
        foreach (FileInfo file in WorkspaceTree.Files(workspace.Root, workspace.Config.ScanExclusions, token, _ => skipped++))
        {
            if (!IsConfiguration(file.Extension))
                continue;

            token.ThrowIfCancellationRequested();
            string owner = file.FullName;
            bool ownerMoves = Inside(owner, source);
            try
            {
                if (file.Length > 4_000_000)
                {
                    skipped++;
                    continue;
                }

                var (references, properties) = ReadReferences(file);
                checkedFiles++;
                foreach (string reference in references.Distinct(StringComparer.Ordinal))
                {
                    string newOwner = Relocate(owner, source, target);
                    string? before = Resolve(reference, owner, properties);
                    string? after = Resolve(reference, newOwner, properties);
                    if (before is null || after is null)
                    {
                        unresolved++;
                        if (ownerMoves || reference.Contains(sourceName, StringComparison.OrdinalIgnoreCase))
                            findings.Add(new("Elle kontrol", Path.GetRelativePath(workspace.Root, owner), reference,
                                "Dinamik başvuru kesin olarak çözümlenemedi."));
                        continue;
                    }

                    bool dependencyMoves = Inside(before, source);
                    if (!ownerMoves && !dependencyMoves)
                        continue;
                    if (!dependencyMoves && !File.Exists(before) && !Directory.Exists(before))
                        continue;

                    string expected = Relocate(before, source, target);
                    if (!Same(after, expected))
                        findings.Add(new("Başvuru etkileniyor", Path.GetRelativePath(workspace.Root, owner), reference,
                            $"Taşındıktan sonra beklenen konum: {expected}"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                skipped++;
            }
        }

        return new(target, checkedFiles, findings, unresolved, skipped);
    }

    internal static bool IsConfiguration(string extension) => extension.ToLowerInvariant() is
        ".csproj" or ".vbproj" or ".fsproj" or ".vcxproj" or ".proj" or ".props" or ".targets" or ".sln" or ".slnx";

    internal static (string[] Paths, Dictionary<string, string?> Properties) ReadReferences(FileInfo file)
    {
        if (file.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            string[] paths = File.ReadLines(file.FullName).Select(line => SlnProject.Match(line))
                .Where(match => match.Success && IsConfiguration(Path.GetExtension(match.Groups["path"].Value)))
                .Select(match => match.Groups["path"].Value).ToArray();
            return (paths, new(StringComparer.OrdinalIgnoreCase));
        }

        using var reader = XmlReader.Create(file.FullName, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_500_000
        });
        var xml = XDocument.Load(reader);
        string[] references = xml.Descendants().Select(element => element.Name.LocalName switch
        {
            "ProjectReference" => (string?)element.Attribute("Include"),
            "Project" => (string?)element.Attribute("Path"),
            "Import" => (string?)element.Attribute("Project"),
            "HintPath" => element.Value,
            _ => null
        }).OfType<string>().Select(path => path.Trim()).Where(path => path.Length > 0).ToArray();

        var properties = xml.Descendants().Where(element => element.Parent?.Name.LocalName == "PropertyGroup" && !element.HasElements)
            .GroupBy(element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group =>
            {
                string[] values = group.Select(item => item.Value.Trim()).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                return values.Length == 1 ? values[0] : null;
            }, StringComparer.OrdinalIgnoreCase);
        return (references, properties);
    }

    internal static string? Resolve(string reference, string owner, Dictionary<string, string?> properties)
    {
        string directory = Path.GetDirectoryName(owner)!;
        string value = reference;
        for (int depth = 0; depth < 8 && Property.IsMatch(value); depth++)
        {
            string expanded = Property.Replace(value, match =>
            {
                string name = match.Groups["name"].Value;
                if (name.Equals("MSBuildThisFileDirectory", StringComparison.OrdinalIgnoreCase))
                    return Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
                if (name.Equals("MSBuildProjectDirectory", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetExtension(owner).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                    return directory;
                return properties.GetValueOrDefault(name) ?? match.Value;
            });
            if (expanded == value)
                break;
            value = expanded;
        }
        if (value.IndexOfAny(['$', '%', '*', '?', ';', '@']) >= 0)
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static void AssertPlainPath(string path, string root)
    {
        for (string? item = path; item is not null && Inside(item, root); item = Path.GetDirectoryName(item))
        {
            if (File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Klasör bağlantıları için taşıma önizlemesi oluşturulamaz.");
            if (Same(item, root))
                break;
        }
    }

    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path, string root) => Same(path, root) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Relocate(string path, string source, string target) =>
        Inside(path, source) ? target + path[source.Length..] : path;
}
