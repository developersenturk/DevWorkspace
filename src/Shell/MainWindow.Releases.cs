using System.IO;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private ReleaseCenterWindow? _releaseCenter;

    private void ReleaseCenterClick(object sender, RoutedEventArgs e)
    {
        if (_releaseCenter is not null) { _releaseCenter.Activate(); return; }
        string? project = (ProjectGrid.SelectedItem as ProjectEntry)?.EntryFile;
        if (project is null || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            string? root = (ProjectGrid.SelectedItem as ProjectEntry)?.Repository ?? _workspace?.Root;
            project = root is null ? null : new[] { Path.Combine(root, "src"), root }.Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.csproj")).FirstOrDefault();
        }
        _releaseCenter = new ReleaseCenterWindow(project) { Owner = this };
        _releaseCenter.Closed += (_, _) => _releaseCenter = null;
        _releaseCenter.Show();
    }
}
