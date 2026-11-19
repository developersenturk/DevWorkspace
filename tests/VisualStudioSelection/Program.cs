using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var assembly = Assembly.Load("DevWorkspace");
        Type Type(string name) => assembly.GetType("DevWorkspace." + name, true)!;
        object? Call(string name, params object?[] values) => Type("VisualStudioInstallations")
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, values);
        object? Property(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value);
        int checks = 0;
        void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }
        string root = Path.Combine(Path.GetTempPath(), "DevWorkspace-vs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string PathFor(string name) => Path.Combine(root, name, "Common7", "IDE", "devenv.exe");
            var available = new HashSet<string>(new[] { "2022", "2026", "SSMS", "Incomplete" }.Select(PathFor), StringComparer.OrdinalIgnoreCase);
            var items = new[]
            {
                new { installationPath = Path.Combine(root, "2022"), displayName = "Visual Studio 2022", installationVersion = "17.14.1", productId = "Microsoft.VisualStudio.Product.Professional", isComplete = true },
                new { installationPath = Path.Combine(root, "2026"), displayName = "Visual Studio 2026", installationVersion = "18.10.1", productId = "Microsoft.VisualStudio.Product.Professional", isComplete = true },
                new { installationPath = Path.Combine(root, "2026"), displayName = "Duplicate", installationVersion = "18.10.1", productId = "Microsoft.VisualStudio.Product.Professional", isComplete = true },
                new { installationPath = Path.Combine(root, "BuildTools"), displayName = "Build Tools", installationVersion = "18.0", productId = "Microsoft.VisualStudio.Product.BuildTools", isComplete = true },
                new { installationPath = Path.Combine(root, "SSMS"), displayName = "SSMS", installationVersion = "22.0", productId = "Microsoft.VisualStudio.Product.Ssms", isComplete = true },
                new { installationPath = Path.Combine(root, "Incomplete"), displayName = "Incomplete", installationVersion = "19.0", productId = "Microsoft.VisualStudio.Product.Professional", isComplete = false }
            };
            string json = JsonSerializer.Serialize(items);
            var installations = (Array)Call("Parse", json, (Func<string, bool>)available.Contains)!;
            Check(installations.Length == 2, "Build Tools, duplicate, incomplete or SSMS installation leaked into the list");
            Check((string?)Property(installations.GetValue(0)!, "Name") == "Visual Studio 2026", "Installations not sorted by version");
            Check((string?)Property(installations.GetValue(1)!, "ExecutablePath") == PathFor("2022"), "Wrong devenv path");
            foreach (string extension in new[] { ".sln", ".slnx", ".csproj", ".vbproj", ".fsproj", ".vcxproj" })
                Check((bool)Call("Supports", "Project" + extension)!, "Unsupported project extension: " + extension);
            Check(!(bool)Call("Supports", "package.json")!, "Non-VS project intercepted");
            string solution = Path.Combine(root, "Folder with spaces & text", "Example.sln");
            var start = (ProcessStartInfo)Call("StartInfo", PathFor("2022"), solution)!;
            Check(start.FileName == PathFor("2022") && start.ArgumentList.Single() == solution, "Wrong executable or unsafe argument construction");
            var system = (ProcessStartInfo)Call("StartInfo", "", solution)!;
            Check(system.FileName == solution && system.ArgumentList.Count == 0 && system.UseShellExecute, "System default launch broken");

            string statePath = Path.Combine(root, "session.json");
            File.WriteAllText(statePath, "{\"Language\":\"en\"}");
            var sessionType = Type("SessionState");
            object Load() => sessionType.GetMethod("LoadFrom", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [statePath])!;
            var session = Load();
            var preferences = (Dictionary<string, string>)Property(session, "VisualStudioPreferences")!;
            Check(preferences.Count == 0, "Old session compatibility broken");
            preferences[solution] = PathFor("2022");
            preferences[solution + "x"] = "";
            Check((bool)sessionType.GetMethod("SaveTo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [statePath])!, "Session save failed");
            var restored = Load();
            var saved = (Dictionary<string, string>)Property(restored, "VisualStudioPreferences")!;
            Check(saved[solution.ToUpperInvariant()] == PathFor("2022"), "Per-project preference or case-insensitive lookup did not survive reload");
            Check(saved[solution + "x"] == "" && (string?)Property(restored, "Language") == "en", "System default or unrelated preferences lost");
            saved[solution] = PathFor("2026");
            sessionType.GetMethod("SaveTo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(restored, [statePath]);
            Check(((Dictionary<string, string>)Property(Load(), "VisualStudioPreferences")!)[solution] == PathFor("2026"), "Changed preference not remembered");

            var app = new Application();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Type("Loc").GetMethod("CreateResources")!.Invoke(null, null)!);
            var pickerType = Type("VisualStudioPickerWindow");
            Window Picker(string? preferred, bool missing) => (Window)Activator.CreateInstance(pickerType,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [solution, installations, preferred, missing], null)!;
            using var cleanup = new WindowCleanup();
            var picker = Picker(PathFor("2022"), false); cleanup.Windows.Add(picker);
            Check((string?)Property(((ListBox)picker.FindName("Installations")).SelectedItem!, "ExecutablePath") == PathFor("2022"), "Picker did not restore saved installation");
            var missingPicker = Picker(PathFor("Removed"), true); cleanup.Windows.Add(missingPicker);
            Check(((TextBlock)missingPicker.FindName("Hint")).Text.Contains((string)Type("Loc").GetMethod("Text")!.Invoke(null, ["VisualStudioMissing"])!), "Missing installation warning absent");
            var systemPicker = Picker("", false); cleanup.Windows.Add(systemPicker);
            Check((string?)Property(((ListBox)systemPicker.FindName("Installations")).SelectedItem!, "ExecutablePath") == "", "Explicit system default not restored");

            var task = (Task)Call("DiscoverAsync", CancellationToken.None)!;
            task.GetAwaiter().GetResult();
            var actual = (Array)task.GetType().GetProperty("Result")!.GetValue(task)!;
            Check(actual.Cast<object>().All(item => File.Exists((string)Property(item, "ExecutablePath")!)), "Discovered IDE executable missing");
            Console.WriteLine($"{checks} Visual Studio checks passed. Detected {actual.Length} installed IDEs.");
            foreach (var item in actual) Console.WriteLine(Property(item!, "Name"));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class WindowCleanup : IDisposable
    {
        internal List<Window> Windows { get; } = [];
        public void Dispose() { foreach (var window in Windows) window.Close(); }
    }
}
