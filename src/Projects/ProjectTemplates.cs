using System.IO;
using System.Text;

namespace DevWorkspace;

internal sealed record ProjectTemplate(string Name, string? Folder, string Kind = "custom");
internal sealed record TemplateFile(string RelativePath, byte[] Content);

internal static class ProjectTemplates
{
    internal static readonly ProjectTemplate[] BuiltIns = [new(".NET Console", null, "console"), new("ASP.NET Core API", null, "web"), new("Node.js", null, "node")];

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            !char.IsAsciiLetter(name[0]) || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new IOException(WorkbenchText.T("NameRule"));
    }

    internal static TemplateFile[] Preview(ProjectTemplate template, string name, CancellationToken token)
    {
        ValidateName(name);
        var files = new List<TemplateFile>();
        void Text(string path, string content) => files.Add(new(path, Encoding.UTF8.GetBytes(content)));
        if (template.Folder is { } folder)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
            long total = 0;
            var skipped = new List<string>();
            foreach (var file in WorkspaceTree.Files(folder, WorkspaceSearch.Exclusions, token, skipped.Add))
            {
                if (file.Name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) && file.Name is not ".env.example" and not ".env.sample" ||
                    file.Extension.ToLowerInvariant() is ".pfx" or ".p12" or ".pem" or ".key" || file.Name.Equals("devworkspace.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Length > 10_000_000 || (total += file.Length) > 50_000_000 || files.Count >= 2000) throw new IOException(WorkbenchText.T("TemplateLimit"));
                string relative = Path.GetRelativePath(folder, file.FullName).Replace("__PROJECT_NAME__", name, StringComparison.Ordinal);
                byte[] content = File.ReadAllBytes(file.FullName);
                // Replace placeholders only in valid UTF-8 text; binary assets are copied unchanged.
                try
                {
                    string text = new UTF8Encoding(false, true).GetString(content);
                    if (!text.Contains('\0')) content = Encoding.UTF8.GetBytes(text.Replace("{{ProjectName}}", name, StringComparison.Ordinal));
                }
                catch (DecoderFallbackException) { }
                files.Add(new(relative, content));
            }
            if (skipped.Count > 0) throw new IOException(WorkbenchText.T("TemplateUnreadable") + " " + skipped[0]);
        }
        else if (template.Kind is "console" or "web")
        {
            Text(name + ".csproj", $"<Project Sdk=\"Microsoft.NET.Sdk{(template.Kind == "web" ? ".Web" : "")}\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
            Text("Program.cs", template.Kind == "web" ? "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/\", () => \"Hello!\");\napp.Run();\n" : "Console.WriteLine(\"Hello!\");\n");
        }
        else if (template.Kind == "node")
        {
            Text("package.json", System.Text.Json.JsonSerializer.Serialize(new { name = name.ToLowerInvariant().Replace('_', '-'), version = "0.1.0", @private = true, scripts = new { start = "node index.js", dev = "node --watch index.js" } }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Text("index.js", "const http = require('node:http');\nconst port = Number(process.env.PORT || 3000);\nhttp.createServer((req, res) => { res.end('Hello!'); }).listen(port, () => console.log(`http://localhost:${port}`));\n");
        }
        else throw new IOException(WorkbenchText.T("TemplateInvalid"));
        if (files.Count == 0) throw new IOException(WorkbenchText.T("TemplateInvalid"));
        if (!files.Any(f => f.RelativePath.Equals("README.md", StringComparison.OrdinalIgnoreCase))) Text("README.md", $"# {name}\n\n{(template.Kind is "console" or "web" ? "dotnet run -c Debug" : template.Kind == "node" ? "npm start" : "")}\n");
        if (!files.Any(f => f.RelativePath.Equals(".gitignore", StringComparison.OrdinalIgnoreCase))) Text(".gitignore", "bin/\nobj/\n.vs/\nnode_modules/\ndist/\n.env\n.env.*\n!.env.example\n!.env.sample\n");
        return files.ToArray();
    }

    internal static async Task<string> CreateAsync(string parent, string name, TemplateFile[] files, bool git, CancellationToken token)
    {
        ValidateName(name);
        parent = Path.GetFullPath(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        string target = Path.Combine(parent, name);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException(WorkbenchText.T("TargetExists"));
        string staging = Path.Combine(parent, ".devworkspace-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(staging, file.RelativePath));
                if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException(WorkbenchText.T("TemplateInvalid"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.CreateNew);
                await output.WriteAsync(file.Content, token);
            }
            if (git)
            {
                var (code, _, error) = await GitProjectOperations.RunAsync(staging, ["init"], token);
                if (code != 0) throw new IOException(error);
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            // Only the uniquely created staging directory belongs to this operation.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
