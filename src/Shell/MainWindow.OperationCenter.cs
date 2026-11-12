using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class MainWindow
{
    private OperationCenterWindow? _operationsWindow;
    internal CancellationToken WorkbenchCancellation => _closing.Token;

    private void OperationCenterClick(object sender, RoutedEventArgs e) => ShowOperationCenter();

    private void ShowOperationCenter()
    {
        if (_operationsWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new OperationCenterWindow { Owner = this };
        _operationsWindow = window;
        window.Closed += (_, _) => _operationsWindow = null;
        window.Show();
    }

    private void LaunchProfilesClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _workspace is not { } workspace || _projects.Count == 0)
            return;
        var window = new LaunchProfilesWindow(_projects, _session.For(workspace.Root).Profiles ?? [], profiles =>
        {
            _session.RememberProfiles(workspace.Root, profiles);
            _session.Save();
        }) { Owner = this };
        if (window.ShowDialog() != true || window.SelectedProfile is not { } profile)
            return;

        StartLaunchProfile(profile);
    }

    private void LaunchProfilesMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource))
            return;
        while (LaunchProfilesMenu.Items.Count > 1)
            LaunchProfilesMenu.Items.RemoveAt(1);
        if (_workspace is not { } workspace || _busy)
            return;
        foreach (var profile in _session.For(workspace.Root).Profiles ?? [])
        {
            var item = new MenuItem { Header = $"Başlat · {profile.Name.Replace("_", "__")}" };
            item.Click += (_, _) => StartLaunchProfile(profile);
            LaunchProfilesMenu.Items.Add(item);
        }
    }

    private void StartLaunchProfile(LaunchProfile profile)
    {
        foreach (var step in profile.Steps)
        {
            var project = _projects.FirstOrDefault(candidate => candidate.RelativePath.Equals(step.Project, StringComparison.OrdinalIgnoreCase));
            if (project is null || !Directory.Exists(project.Folder))
            {
                OperationCenter.Start(profile.Name, step.Project).Finish("Başarısız", "Proje klasörü bulunamadı.");
                continue;
            }
            if (step.Command is { } command)
                _ = OperationCenter.RunCommandAsync(command, project.Name, project.Folder, _closing.Token);
            else
                OpenProject(project);
        }
        ShowOperationCenter();
    }
}
