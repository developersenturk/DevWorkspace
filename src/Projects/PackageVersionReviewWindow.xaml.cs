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
            ReviewExplanation.Text = "Dolaylı açıklar için önerilen sürümler geçici bir kopyada doğrulandı. " + ReviewExplanation.Text;
        ChangesList.ItemsSource = plan.Changes.DistinctBy(change => change.Name).Select(change =>
        {
            string text = $"{change.Name}   {change.Current} → {change.Target}" +
                (change.Current.Major != change.Target.Major ? "   · Ana sürüm değişikliği" : "");
            if (change.CurrentConstraint is { } constraint && constraint != change.Current.ToString())
                text += $"\nSürüm kuralı   {constraint} → {change.Target}";
            if (change.FixedDependencies is { Length: > 0 } dependencies)
                text += $"\nAçığı giderilecek dolaylı paketler   {string.Join(", ", dependencies)}";
            return text;
        }).ToArray();
    }

    private void ApproveClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
