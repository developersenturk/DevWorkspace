using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class MainWindow
{
    private OperationCenterWindow? _operationsWindow;
    private bool _startingProfile;
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
        }, workspace.Root) { Owner = this };
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
            var item = new MenuItem { Header = Loc.Format("LDBF5A5D7DF9A", profile.Name.Replace("_", "__")) };
            item.Click += (_, _) => StartLaunchProfile(profile);
            LaunchProfilesMenu.Items.Add(item);
        }
    }

    private async void StartLaunchProfile(LaunchProfile profile)
    {
        if (_startingProfile || _busy || _workspace is null) return;
        _startingProfile = true;
        try
        {
        UsageMetrics.Record(_session, "profile_run");
        string? root = _workspace?.Root;
        foreach (var step in profile.Steps)
        {
            if (_closing.IsCancellationRequested || _workspace?.Root != root) return;
            var project = _projects.FirstOrDefault(candidate => candidate.RelativePath.Equals(step.Project, StringComparison.OrdinalIgnoreCase));
            if (project is null || !Directory.Exists(project.Folder))
            {
                OperationCenter.Start(profile.Name, step.Project).Finish(Loc.Text("LF0873175A881"), Loc.Text("LAC678EB688A2"));
                continue;
            }
            if (step.Action == "terminal")
                OpenProjectTerminal(project);
            else if (step.Command is { } command)
                _ = OperationCenter.RunCommandAsync(command, project.Name, project.Folder, _closing.Token);
            else
                await OpenProjectAsync(project);
        }
        if (!_closing.IsCancellationRequested) ShowWorkspaceTools(2);
        }
        finally { _startingProfile = false; }
    }
}
