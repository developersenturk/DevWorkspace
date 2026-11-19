using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DevWorkspace;

internal sealed record VisualStudioInstallation(string Name, string ExecutablePath, Version Version)
{
    public string Detail => ExecutablePath.Length == 0 ? Loc.Text("VisualStudioSystemHint") : ExecutablePath;
}

internal static class VisualStudioInstallations
{
    internal static bool Supports(string? path) => path is not null &&
        Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj" or ".vbproj" or ".fsproj" or ".vcxproj";

    internal static async Task<VisualStudioInstallation[]> DiscoverAsync(CancellationToken token)
    {
        var installations = new List<VisualStudioInstallation>();
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            var info = ProjectTools.CreateCaptureInfo(vswhere, Path.GetDirectoryName(vswhere)!,
                ["-all", "-prerelease", "-legacy", "-products", "*", "-format", "json", "-utf8"]);
            try
            {
                var result = await ProjectTools.CaptureAsync(info, "Visual Studio", 15, token);
                if (result.Code == 0) installations.AddRange(Parse(result.Output, File.Exists));
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or JsonException) { }
        }
        // Older installations and machines without the Installer can still expose SxS entries.
        await Task.Run(() =>
        {
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\SxS\VS7");
                    foreach (string name in key?.GetValueNames() ?? [])
                    {
                        if (key?.GetValue(name) is not string folder || !Path.IsPathFullyQualified(folder)) continue;
                        string executable = Path.Combine(folder, "Common7", "IDE", "devenv.exe");
                        if (File.Exists(executable)) installations.Add(new("Visual Studio " + name, executable,
                            Version.TryParse(name, out var version) ? version : new()));
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
        }, token);
        return installations.DistinctBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Version).ThenBy(item => item.Name).ToArray();
    }

    internal static VisualStudioInstallation[] Parse(string json, Func<string, bool> exists)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<VisualStudioInstallation>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? Value(string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (item.TryGetProperty("isComplete", out var complete) && complete.ValueKind == JsonValueKind.False ||
                item.TryGetProperty("isLaunchable", out var launchable) && launchable.ValueKind == JsonValueKind.False) continue;
            if (Value("productId") is { } product && !new[]
                {
                    "Microsoft.VisualStudio.Product.Community", "Microsoft.VisualStudio.Product.Professional",
                    "Microsoft.VisualStudio.Product.Enterprise"
                }.Contains(product, StringComparer.OrdinalIgnoreCase)) continue;
            if (Value("installationPath") is not { } folder || !Path.IsPathFullyQualified(folder)) continue;
            try
            {
                string executable = Path.GetFullPath(Path.Combine(folder, "Common7", "IDE", "devenv.exe"));
                if (!exists(executable)) continue; // Build Tools has no IDE.
                result.Add(new(Value("displayName") ?? "Visual Studio", executable,
                    Version.TryParse(Value("installationVersion"), out var version) ? version : new()));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return result.DistinctBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase).OrderByDescending(item => item.Version).ToArray();
    }

    internal static ProcessStartInfo StartInfo(string executable, string projectPath)
    {
        var info = new ProcessStartInfo(executable.Length == 0 ? projectPath : executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!
        };
        if (executable.Length > 0) info.ArgumentList.Add(projectPath);
        return info;
    }
}
