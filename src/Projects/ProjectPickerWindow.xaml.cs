using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevWorkspace;

public partial class ProjectPickerWindow : Window
{
    private readonly ProjectEntry[] _ordered;
    internal ProjectEntry? Chosen { get; private set; }

    internal ProjectPickerWindow(IEnumerable<ProjectEntry> projects, IReadOnlyList<string> recent, string title, string action,
        ProjectEntry? baseline = null)
    {
        InitializeComponent();
        Title = title;
        ChooseButton.Content = action;
        var recentOrder = recent.Select((path, index) => (path, index)).DistinctBy(item => item.path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.path, item => item.index, StringComparer.OrdinalIgnoreCase);
        _ordered = projects.OrderByDescending(project => baseline is not null && (
                project.Name.Equals(baseline.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(baseline.Preference.Group) &&
                string.Equals(project.Preference.Group, baseline.Preference.Group, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(project => project.Preference.Favorite)
            .ThenBy(project => recentOrder.GetValueOrDefault(project.RelativePath, int.MaxValue))
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Filter();
    }

    private void PickerLoaded(object sender, RoutedEventArgs e) => QueryBox.Focus();

    private void QueryChanged(object sender, TextChangedEventArgs e)
    {
        if (_ordered is not null)
            Filter();
    }

    private void Filter()
    {
        string query = QueryBox.Text.Trim();
        var matches = _ordered.Where(project => query.Length == 0 ||
            project.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            project.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (project.Preference.Group ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(project => query.Length > 0 && project.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(80).ToArray();
        Results.ItemsSource = matches;
        Results.SelectedIndex = matches.Length > 0 ? 0 : -1;
        ChooseButton.IsEnabled = matches.Length > 0;
        PickerHint.Text = matches.Length == 0 ? "Eşleşen proje yok." : "↑ ↓ seç · Enter onayla · Esc kapat";
    }

    private void PickerKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                DialogResult = false;
                break;
            case Key.Enter:
                e.Handled = true;
                Choose();
                break;
            case Key.Up:
            case Key.Down:
                if (Results.Items.Count == 0)
                    return;
                e.Handled = true;
                Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Results.Items.Count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
        }
    }

    private void ChooseClick(object sender, RoutedEventArgs e) => Choose();
    private void ResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Results, source) is ListBoxItem)
            Choose();
    }

    private void Choose()
    {
        if (Results.SelectedItem is ProjectEntry project)
        {
            Chosen = project;
            DialogResult = true;
        }
    }
}
