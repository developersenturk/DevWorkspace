using System.Windows;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class SettingsWindow : ThemedWindow
{
    private sealed record LanguageChoice(string Code, string Label);
    internal string SelectedLanguage => LanguagePicker.SelectedValue as string ?? "tr";

    internal SettingsWindow(string language)
    {
        InitializeComponent();
        LanguagePicker.ItemsSource = new[] { new LanguageChoice("tr", "Türkçe"), new LanguageChoice("en", "English") };
        LanguagePicker.SelectedValue = Loc.NormalizeLanguage(language);
    }

    private void SaveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
