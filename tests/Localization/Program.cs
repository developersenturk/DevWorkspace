using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var assembly = Assembly.Load("DevWorkspace");
        Type Type(string name) => assembly.GetType("DevWorkspace." + name, true)!;
        object? Call(string type, string name, params object?[] args) => Type(type).GetMethod(name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        Dictionary<string, string> Catalog(string language)
        {
            using var stream = assembly.GetManifestResourceStream($"DevWorkspace.Localization.{language}.json")!;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        var tr = Catalog("tr"); var en = Catalog("en");
        Check(tr.Keys.ToHashSet().SetEquals(en.Keys), "Catalog keys differ");
        foreach (string key in tr.Keys)
        {
            Check(!string.IsNullOrWhiteSpace(en[key]), "Empty English translation: " + key);
            var a = CompositeFormat.Parse(tr[key]); var b = CompositeFormat.Parse(en[key]);
            Check(a.MinimumArgumentCount == b.MinimumArgumentCount, "Format arguments differ: " + key);
            var arguments = Enumerable.Repeat<object>(new FormatProbe(), a.MinimumArgumentCount).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, a, arguments);
            _ = string.Format(CultureInfo.InvariantCulture, b, arguments);
        }
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml")
            .Where(path => !path.Contains("\\obj\\") && !path.Contains("\\bin\\")))
        {
            string source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, "Loc\\.(?:Text|Format)\\(\"(?<key>[^\"]+)\"|\\{DynamicResource (?<key>L[A-F0-9]{12}|SettingsTitle|InterfaceLanguage|LanguageRestartHint|SaveAndRestart|CancelSettings|WindowsMenuTitle|Update[A-Za-z]+)\\}"))
                Check(tr.ContainsKey(match.Groups["key"].Value) ||
                    ((ResourceDictionary)Call("AppUpdateText", "CreateResources")!).Contains(match.Groups["key"].Value),
                    "Missing resource in " + file + ": " + match.Value);
        }

        var sessionType = Type("SessionState");
        string temporary = Path.Combine(Path.GetTempPath(), "DevWorkspace-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string file = Path.Combine(temporary, "session.json");
            File.WriteAllText(file, "{\"DarkTheme\":true,\"LastWorkspace\":\"D:\\\\Projects\"}");
            var legacy = Call("SessionState", "LoadFrom", file)!;
            Check((string?)sessionType.GetProperty("Language")!.GetValue(legacy) == "tr", "Legacy language default");
            foreach (string language in new[] { "en", "tr" })
            {
                sessionType.GetProperty("Language")!.SetValue(legacy, language);
                Check((bool)sessionType.GetMethod("SaveTo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(legacy, [file])!, "Could not save preferences");
                var restored = Call("SessionState", "LoadFrom", file)!;
                Check((string?)sessionType.GetProperty("Language")!.GetValue(restored) == language, "Language did not persist");
                Check((bool)sessionType.GetProperty("DarkTheme")!.GetValue(restored)!, "Other settings lost");
                Call("Loc", "Initialize", language);
                Check((string?)Call("Loc", "Text", "SettingsTitle") == (language == "en" ? "Settings" : "Ayarlar"), "Wrong translation");
                Check(CultureInfo.CurrentCulture.Name == (language == "en" ? "en-US" : "tr-TR"), "Wrong culture");
                // Test a real, discovered numeric message instead of a hard-coded catalog key.
                string numericKey = tr.First(pair => pair.Value == "{0:N0} proje").Key;
                string formatted = (string)Call("Loc", "Format", numericKey, new object?[] { 1234 })!;
                Check(formatted.Contains(language == "en" ? "1,234" : "1.234"), "Wrong number formatting");
            }
            File.WriteAllText(file, "{\"Language\":\"unsupported\"}");
            Check((string?)sessionType.GetProperty("Language")!.GetValue(Call("SessionState", "LoadFrom", file)) == "tr", "Invalid language fallback");
        }
        finally { Directory.Delete(temporary, true); }

        var app = new Application();
        foreach (string language in new[] { "en", "tr" })
        {
            Call("Loc", "Initialize", language);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Call("Loc", "CreateResources")!);
            var window = (Window)Activator.CreateInstance(Type("SettingsWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [language], CultureInfo.InvariantCulture)!;
            var picker = (ComboBox)window.FindName("LanguagePicker");
            Check((string?)picker.SelectedValue == language && picker.Items.Count == 2, "Language picker did not restore selection");
            Check(window.Title == (language == "en" ? "Settings" : "Ayarlar"), "Settings window title not translated");
            window.Close();
            const string report = """{"projects":[{"path":"test.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":"Example","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"High","advisoryurl":"https://example.test/advisory"}]}]}]}]}""";
            var result = Call("ProjectIntegrations", "ParseNuGetReport", report, "")!;
            var findings = ((System.Collections.IEnumerable)result.GetType().GetProperty("Findings")!.GetValue(result)!).Cast<object>().ToArray();
            Check(findings.Length == 1 && (string?)findings[0].GetType().GetProperty("Severity")!.GetValue(findings[0]) == "Yüksek", "Stored severity depends on language");
            Check((string?)findings[0].GetType().GetProperty("SeverityLabel")!.GetValue(findings[0]) == (language == "en" ? "High" : "Yüksek"), "Severity display not translated");
            var operation = Activator.CreateInstance(Type("WorkspaceOperation"), "Test", "Target", null)!;
            Check((bool)operation.GetType().GetProperty("IsRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operation)!, "Operation state depends on language");
        }
        Console.WriteLine($"{checks} localization checks passed across {tr.Count} messages.");
    }

    private sealed class FormatProbe : IFormattable
    {
        public string ToString(string? format, IFormatProvider? provider) => "value";
        public override string ToString() => "value";
    }
}
