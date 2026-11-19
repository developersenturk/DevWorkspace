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
    public static void Main(string[] args)
    {
        var assembly = Assembly.Load("DevWorkspace");
        Type Type(string name) => assembly.GetType("DevWorkspace." + name, true)!;
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(Type("App").TypeHandle);
        object? Call(string type, string name, params object?[] args) => Type(type).GetMethod(name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
        if (args is ["--release-preview", var previewLanguage, var previewTheme])
        {
            var previewApp = (Application)Activator.CreateInstance(Type("App"))!;
            Call("Loc", "Initialize", previewLanguage);
            var messages = previewApp.Resources.MergedDictionaries.First(d => d.Contains("SettingsTitle"));
            previewApp.Resources.MergedDictionaries[previewApp.Resources.MergedDictionaries.IndexOf(messages)] = (ResourceDictionary)Call("Loc", "CreateResources")!;
            Call("App", "ApplyTheme", previewTheme == "light" ? DevExpress.Xpf.Core.LightweightTheme.Win11Light : DevExpress.Xpf.Core.LightweightTheme.Win11Dark);
            string project = Path.GetFullPath("src/DevWorkspace.csproj");
            var preview = (Window)Activator.CreateInstance(Type("ReleaseCenterWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object?[] { File.Exists(project) ? project : null }, CultureInfo.InvariantCulture)!;
            preview.Width = 960; preview.Height = 720;
            previewApp.MainWindow = preview;
            previewApp.ShutdownMode = ShutdownMode.OnMainWindowClose;
            previewApp.Run(preview);
            return;
        }
        int checks = 0;
        Console.WriteLine("Localization: catalogs and source resources");
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

        Console.WriteLine("Localization: release solution resolution");
        string releaseFixture = Path.Combine(Path.GetTempPath(), "DevWorkspace-release-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseFixture);
        try
        {
            string firstApp = Path.Combine(releaseFixture, "First.csproj"), secondApp = Path.Combine(releaseFixture, "Second.csproj");
            File.WriteAllText(firstApp, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            File.WriteAllText(secondApp, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
            string firstSolution = Path.Combine(releaseFixture, "First.slnx");
            File.WriteAllText(firstSolution, "<Solution><Project Path=\"First.csproj\" /></Solution>");
            string[] Candidates(string entry) => ((IEnumerable<string>)Call("ProjectTools", "FindReleaseProjects", entry)!).ToArray();
            Check(Candidates(firstApp).SequenceEqual(new[] { firstApp }), "A directly selected project must be used exactly");
            Check(Candidates(firstSolution).SequenceEqual(new[] { firstApp }), "Solution context must ignore another application beside the solution");
            File.WriteAllText(firstSolution, "<Solution><Project Path=\"First.csproj\" /><Project Path=\"Second.csproj\" /></Solution>");
            Check(Candidates(firstSolution).ToHashSet().SetEquals(new[] { firstApp, secondApp }), "Multiple application choices must be preserved instead of selecting an arbitrary project");
            Check(Candidates(Path.Combine(releaseFixture, "Missing.slnx")).Length == 0, "Missing project must not fall back to another application");
            string testFolder = Path.Combine(releaseFixture, "tests", "ConsoleChecks"); Directory.CreateDirectory(testFolder);
            string consoleTest = Path.Combine(testFolder, "ConsoleChecks.csproj");
            File.WriteAllText(consoleTest, "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
            string sdkTest = Path.Combine(releaseFixture, "Checks.csproj");
            File.WriteAllText(sdkTest, "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>");
            string namedTest = Path.Combine(releaseFixture, "Product.Tests.csproj");
            File.WriteAllText(namedTest, "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
            Check(Candidates(consoleTest).Length == 0 && Candidates(sdkTest).Length == 0 && Candidates(namedTest).Length == 0,
                "Test projects must not be directly publishable");
            File.WriteAllText(firstSolution, "<Solution><Project Path=\"First.csproj\" /><Project Path=\"tests/ConsoleChecks/ConsoleChecks.csproj\" /><Project Path=\"Checks.csproj\" /><Project Path=\"Product.Tests.csproj\" /></Solution>");
            Check(Candidates(firstSolution).SequenceEqual(new[] { firstApp }), "Release menu must exclude test targets from a mixed solution");
        }
        finally { Directory.Delete(releaseFixture, true); }

        if (args is ["--headless"])
        {
            Console.WriteLine($"{checks} headless localization and solution-context checks passed across {tr.Count} messages.");
            Console.WriteLine("Desktop window/dispatcher checks run in the default mode on a desktop host; --headless does not instantiate windows or pump WPF messages.");
            return;
        }
        Console.WriteLine("Localization: WPF application and context switching");
        var app = new Application();
        // Exercise real asynchronous project switching with isolated local repositories and storage; no network or visible window.
        string contextRoot = Path.Combine(Path.GetTempPath(), "DevWorkspace-release-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contextRoot);
        try
        {
            string CreateContext(string name)
            {
                string folder = Path.Combine(contextRoot, name); Directory.CreateDirectory(folder);
                string projectFile = Path.Combine(folder, name + ".csproj");
                File.WriteAllText(projectFile, "<Project><PropertyGroup><OutputType>WinExe</OutputType><Version>1.2.3</Version></PropertyGroup></Project>");
                foreach (string[] command in new[] { new[] { "init" }, new[] { "remote", "add", "origin", "https://github.com/test-owner/" + name + ".git" } })
                {
                    var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = folder, CreateNoWindow = true,
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (string argument in command) info.ArgumentList.Add(argument);
                    using var git = System.Diagnostics.Process.Start(info)!;
                    if (!git.WaitForExit(10000)) { git.Kill(entireProcessTree: true); throw new Exception("Git fixture command timed out"); }
                    Check(git.ExitCode == 0, "Could not prepare local context fixture");
                }
                return projectFile;
            }
            string contextA = CreateContext("First"), contextB = CreateContext("Second");
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Call("Loc", "CreateResources")!);
            var isolatedStore = Activator.CreateInstance(Type("ReleaseStore"), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object?[] { Path.Combine(contextRoot, "state") }, CultureInfo.InvariantCulture)!;
            Console.WriteLine("Localization: construct contextual WPF window");
            var contextWindow = (Window)Activator.CreateInstance(Type("ReleaseCenterWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object?[] { null, isolatedStore }, CultureInfo.InvariantCulture)!;
            void SwitchTo(string projectFile)
            {
                Console.WriteLine("Localization: switch to " + Path.GetFileName(projectFile));
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(contextWindow.Dispatcher));
                try
                {
                var task = (Task)Type("ReleaseCenterWindow").GetMethod("SelectProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(contextWindow, [projectFile])!;
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                timeout.Tick += (_, _) => frame.Continue = false;
                timeout.Start();
                _ = task.ContinueWith(_ => contextWindow.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                try { if (!task.IsCompleted) System.Windows.Threading.Dispatcher.PushFrame(frame); }
                finally { timeout.Stop(); }
                Check(task.IsCompleted, "Project context switch timed out: " + Path.GetFileName(projectFile));
                task.GetAwaiter().GetResult();
                }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            }
            TextBox Field(string name) => (TextBox)contextWindow.FindName(name);
            SwitchTo(contextA);
            Check(Field("ProjectFile").Text == contextA && Field("Repository").Text == "test-owner/First", "First project context not detected");
            Field("Notes").Text = "First project's unsaved notes";
            ((Button)contextWindow.FindName("UploadButton")).IsEnabled = ((Button)contextWindow.FindName("PublishButton")).IsEnabled = true;
            ((TextBlock)contextWindow.FindName("DownloadTotal")).Text = "99";
            SwitchTo(contextB);
            Check(Field("ProjectFile").Text == contextB && Field("Repository").Text == "test-owner/Second" && Field("AppId").Text == "Second", "Open window did not switch to the exact new project");
            Check(Field("Notes").Text.Length == 0 && ((TextBlock)contextWindow.FindName("DownloadTotal")).Text == "—", "Old project's notes or statistics leaked into new context");
            Check(!((Button)contextWindow.FindName("UploadButton")).IsEnabled && !((Button)contextWindow.FindName("PublishButton")).IsEnabled, "Old package actions remained enabled after switching project");
            SwitchTo(contextA);
            Check(Field("Notes").Text == "First project's unsaved notes" && Field("Repository").Text == "test-owner/First",
                "Returning to a project must restore its own automatically saved notes");
            SwitchTo(Path.Combine(contextRoot, "Missing.csproj"));
            Check(Field("ProjectFile").Text.Length == 0 && Field("Repository").Text.Length == 0 &&
                !((Button)contextWindow.FindName("PublishButton")).IsEnabled, "Failed detection retained another project's publish target");
            contextWindow.Close();
        }
        finally { Directory.Delete(contextRoot, true); }
        foreach (string language in new[] { "en", "tr" })
        {
            Console.WriteLine("Localization: themed windows in " + language);
            Call("Loc", "Initialize", language);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Call("Loc", "CreateResources")!);
            var window = (Window)Activator.CreateInstance(Type("SettingsWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [language], CultureInfo.InvariantCulture)!;
            var picker = (ComboBox)window.FindName("LanguagePicker");
            Check((string?)picker.SelectedValue == language && picker.Items.Count == 2, "Language picker did not restore selection");
            Check(window.Title == (language == "en" ? "Settings" : "Ayarlar"), "Settings window title not translated");
            window.Close();
            var releaseWindow = (Window)Activator.CreateInstance(Type("ReleaseCenterWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object?[] { null }, CultureInfo.InvariantCulture)!;
            Check(releaseWindow.Title == (language == "en" ? "Release Center" : "Yayın Merkezi"), "Release center title not translated");
            Check(releaseWindow.FindResource("SmallLabel") is Style, "Release center shared styles missing");
            Check(((TextBox)releaseWindow.FindName("ProjectFile")).Text.Length == 0, "Unbound release window inherited a global project");
            foreach (string contextField in new[] { "ProjectFile", "Repository", "AppId" })
                Check(((TextBox)releaseWindow.FindName(contextField)).IsReadOnly, "Bound project identity must not be editable: " + contextField);
            Check(((Button)releaseWindow.FindName("UploadButton")).IsEnabled == false &&
                ((Button)releaseWindow.FindName("PublishButton")).IsEnabled == false, "No-package actions must be disabled");
            foreach (string field in new[] { "ProjectFile", "Repository", "AppId", "VersionNumber", "Notes", "TestProjects" })
                Check(releaseWindow.FindName(field) is TextBox, "Release field missing: " + field);
            releaseWindow.Close();
            foreach (var theme in new[] { DevExpress.Xpf.Core.LightweightTheme.Win11Light, DevExpress.Xpf.Core.LightweightTheme.Win11Dark })
            {
                Call("App", "ApplyTheme", theme);
                app.Resources.MergedDictionaries.Add((ResourceDictionary)Call("App", "CreatePalette", theme)!);
                app.Resources.MergedDictionaries.Add((ResourceDictionary)Call("AppUpdateText", "CreateResources")!);
                using var notesClient = new System.Net.Http.HttpClient();
                var notesService = Activator.CreateInstance(Type("AppReleaseNotes"), notesClient, Path.Combine(contextRoot, "notes"));
                var notesWindow = (Window)Activator.CreateInstance(Type("WhatsNewWindow"), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [notesService], CultureInfo.InvariantCulture)!;
                Check(notesWindow.Title == (language == "en" ? "What's new" : "Yenilikler"), "News title must match language in both themes");
                var reader = (FlowDocumentScrollViewer)notesWindow.FindName("NotesReader");
                string noteText = new System.Windows.Documents.TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text;
                Check(noteText.Contains(language == "en" ? "Release notes inside the app" : "Uygulama içinden sürüm notları"), "Bundled Markdown must render in the selected language");
                foreach (string resource in new[] { "Ink", "Muted", "PanelSoft", "Divider" })
                    Check(notesWindow.FindResource(resource) is System.Windows.Media.Brush, "News theme brush missing: " + resource);
                notesWindow.Close();
            }
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
