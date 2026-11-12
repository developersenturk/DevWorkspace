using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

internal sealed record ProjectPickerAction(string Label, string Keywords, Func<ProjectEntry, bool> Supports,
    Action<ProjectEntry> Execute);

internal sealed record ProjectPickerResult(ProjectEntry Project, ProjectPickerAction? Action)
{
    public string Name => Action is null ? Project.Name : $"{Project.Name} · {Action.Label}";
}

public partial class ProjectPickerWindow : ThemedWindow
{
    private readonly ProjectEntry[] _ordered;
    private readonly IReadOnlyList<ProjectPickerAction> _actions;
    internal ProjectEntry? Chosen { get; private set; }
    internal ProjectPickerAction? ChosenAction { get; private set; }

    internal ProjectPickerWindow(IEnumerable<ProjectEntry> projects, IReadOnlyList<string> recent, string title, string action,
        ProjectEntry? baseline = null, IReadOnlyList<ProjectPickerAction>? actions = null)
    {
        InitializeComponent();
        Title = title;
        ChooseButton.Content = action;
        _actions = actions ?? [];
        if (_actions.Count > 0)
        {
            QueryBox.ToolTip = "Proje veya işlem ara · Örnek: Billing diff";
            System.Windows.Automation.AutomationProperties.SetName(QueryBox, "Proje veya işlem ara");
        }
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
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var projects = _ordered.Select(project => new ProjectPickerResult(project, null));
        IEnumerable<ProjectPickerResult> commands = query.Length == 0 ? [] : _ordered.SelectMany(project =>
            _actions.Where(action => action.Supports(project)).Select(action => new ProjectPickerResult(project, action)));
        var matches = projects.Concat(commands).Where(result => Matches(result, query, terms))
            .OrderByDescending(result => query.Length > 0 && result.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(80).ToArray();
        Results.ItemsSource = matches;
        Results.SelectedIndex = matches.Length > 0 ? 0 : -1;
        ChooseButton.IsEnabled = matches.Length > 0;
        PickerHint.Text = matches.Length == 0 ? "Eşleşme yok." : "↑ ↓ seç · Enter onayla · Esc kapat";
    }

    private static bool Matches(ProjectPickerResult result, string query, string[] terms)
    {
        var project = result.Project;
        bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);
        bool MatchesProject(string term) => Contains(project.Name, term) || Contains(project.DisplayPath, term) ||
            Contains(project.Preference.Group ?? "", term);
        if (result.Action is not { } action)
            return MatchesProject(query);

        string command = $"{action.Label} {action.Keywords}";
        return terms.Any(term => Contains(command, term)) &&
            terms.All(term => MatchesProject(term) || Contains(command, term));
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
                if (Results.ItemsSource is not ProjectPickerResult[] { Length: > 0 } matches)
                    return;
                e.Handled = true;
                Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, matches.Length - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                break;
        }
    }

    private void ChooseClick(object sender, RoutedEventArgs e) => Choose();
    private void ResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: ProjectPickerResult result })
        {
            e.Handled = true;
            Results.SelectedItem = result;
            Choose();
        }
    }

    private void Choose()
    {
        if (Results.SelectedItem is ProjectPickerResult result)
        {
            Chosen = result.Project;
            ChosenAction = result.Action;
            DialogResult = true;
        }
    }
}
