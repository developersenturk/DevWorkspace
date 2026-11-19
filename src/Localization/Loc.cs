using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace DevWorkspace;

internal static class Loc
{
    private static readonly IReadOnlyDictionary<string, string> Turkish = Load("tr");
    private static readonly IReadOnlyDictionary<string, string> English = Load("en");
    public static string Language { get; private set; } = "tr";
    public static string NormalizeLanguage(string? language) => language is "en" ? "en" : "tr";

    public static void Initialize(string? language)
    {
        Language = NormalizeLanguage(language);
        var culture = CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "tr-TR");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
    }

    public static string Text(string key) =>
        Language == "en" && English.TryGetValue(key, out var translation) ? translation :
        Turkish.TryGetValue(key, out var original) ? original : key;
    public static string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    public static string Label(string source)
    {
        string key = "L" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
        return Turkish.ContainsKey(key) ? Text(key) : source;
    }

    public static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(AppUpdateText.CreateResources());
        foreach (string key in Turkish.Keys) resources[key] = Text(key);
        return resources;
    }

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DevWorkspace.Localization.{language}.json")
            ?? throw new InvalidOperationException($"Missing localization resource: {language}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new InvalidOperationException("Invalid localization catalog.");
    }
}
