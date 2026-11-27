using System.Windows;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class SettingsWindow : ThemedWindow
{
    private sealed record LanguageChoice(string Code, string Label);
    internal string SelectedLanguage => LanguagePicker.SelectedValue as string ?? "tr";
    internal bool OpenDiffOnDoubleClick => DoubleClickDiff.IsChecked == true;

    internal SettingsWindow(string language)
        : this(language, true) { }

    internal SettingsWindow(string language, bool openDiffOnDoubleClick)
    {
        InitializeComponent();
        LanguagePicker.ItemsSource = new[] { new LanguageChoice("tr", "Türkçe"), new LanguageChoice("en", "English") };
        LanguagePicker.SelectedValue = Loc.NormalizeLanguage(language);
        DoubleClickDiff.IsChecked = openDiffOnDoubleClick;
    }

    private void SaveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
