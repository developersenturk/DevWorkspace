using System.Windows;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class PackageVersionReviewWindow : ThemedWindow
{
    internal PackageVersionReviewWindow(string project, PackageVersionPlan plan)
    {
        InitializeComponent();
        ProjectLabel.Text = project;
        ManifestLabel.Text = plan.Manifest;
        if (plan.ExpectedFixes.Count > 0)
            ReviewExplanation.Text = Loc.Text("L29938568F5F3") + ReviewExplanation.Text;
        ChangesList.ItemsSource = plan.Changes.DistinctBy(change => change.Name).Select(change =>
        {
            string text = $"{change.Name}   {change.Current} → {change.Target}" +
                (change.Current.Major != change.Target.Major ? Loc.Text("L9AE13DB02A53") : "");
            if (change.CurrentConstraint is { } constraint && constraint != change.Current.ToString())
                text += Loc.Format("L38D64C797A87", constraint, change.Target);
            if (change.FixedDependencies is { Length: > 0 } dependencies)
                text += Loc.Format("LA138458F4E78", string.Join(", ", dependencies));
            return text;
        }).ToArray();
    }

    private void ApproveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
