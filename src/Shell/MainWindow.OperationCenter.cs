using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace DevWorkspace;

public partial class MainWindow
{
    private OperationCenterWindow? _operationsWindow;
    private bool _startingProfile;
    private readonly Dictionary<string, DevelopmentRun> _developmentRuns = new(StringComparer.OrdinalIgnoreCase);
    private DevelopmentHubWindow? _developmentHub;
    private bool _finishingDevelopmentOnClose;
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
        try { await RunDevelopmentProfileAsync(profile); if (!_closing.IsCancellationRequested) ShowWorkspaceTools(2); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.Xml.XmlException)
        { OperationCenter.Start(profile.Name, _workspace?.Root ?? "").Finish(Loc.Text("LF0873175A881"), ex.Message); StatusText.Text = ex.Message; ShowOperationCenter(); }
    }

    private async Task RunDevelopmentProfileAsync(LaunchProfile profile, bool preflightCompleted = false)
    {
        if (_startingProfile || _busy || _workspace is not { } workspace) throw new InvalidOperationException(WorkbenchText.T("AlreadyRunning"));
        string key = workspace.Root + "\n" + profile.Name;
        if (_developmentRuns.TryGetValue(key, out var existing))
        {
            if (existing.IsActive) throw new IOException(WorkbenchText.T("AlreadyRunning"));
            existing.Dispose(); _developmentRuns.Remove(key);
        }
        _startingProfile = true;
        var run = new DevelopmentRun(_closing.Token); _developmentRuns[key] = run;
        try
        {
            UsageMetrics.Record(_session, "profile_run");
            if (!preflightCompleted)
            {
                StatusText.Text = WorkbenchText.T("Working");
                var projects = _projects.ToArray();
                var requiredTools = _session.For(workspace.Root).RequiredTools ?? [];
                var rows = await Task.Run(() => DevelopmentPreflight.ReadAsync(workspace, projects, profile,
                    requiredTools, run.Token), run.Token);
                if (rows.Any(DevelopmentPreflight.Blocks)) throw new IOException(WorkbenchText.T("PreflightBlocked"));
            }
            await run.StartAsync(profile, _projects.ToArray(), OpenProjectAsync, OpenProjectTerminal, message => StatusText.Text = message);
            _session.Workspaces[workspace.Root] = _session.For(workspace.Root) with { LastProfile = profile.Name };
            _session.Save();
        }
        catch { run.Stop(); throw; }
        finally { _startingProfile = false; }
    }

    private async void StopDevelopment()
    {
        if (_workspace is not { } workspace) return;
        var runs = _developmentRuns.Where(p => p.Key.StartsWith(workspace.Root + "\n", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToArray();
        foreach (var run in runs) run.Stop();
        await Task.WhenAll(runs.Select(r => r.StopCompletion));
    }

    private async Task StopDevelopmentAndCloseAsync()
    {
        var runs = _developmentRuns.Values.ToArray();
        foreach (var run in runs) run.Stop();
        await Task.WhenAll(runs.Select(r => r.StopCompletion));
        if (!Dispatcher.HasShutdownStarted) Close();
    }

    private void DevelopmentHubClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _workspace is not { } workspace) return;
        if (_developmentHub is { } existing) { existing.WindowState = WindowState.Normal; existing.Activate(); return; }
        var window = new DevelopmentHubWindow(workspace, () => _projects.ToArray(), () => _session.For(workspace.Root), (profiles, requiredTools) =>
        {
            var previous = _session.For(workspace.Root); _session.RememberProfiles(workspace.Root, profiles);
            _session.Workspaces[workspace.Root] = _session.For(workspace.Root) with { RequiredTools = requiredTools ?? previous.RequiredTools };
            if (_session.SaveTo(SessionState.FilePath)) return true;
            _session.Workspaces[workspace.Root] = previous; return false;
        }, profile => RunDevelopmentProfileAsync(profile, preflightCompleted: true), StopDevelopment, () => LaunchProfilesClick(this, new RoutedEventArgs()), ShowWorkspaceTools,
        OpenProjectAsync, OpenGitWorkbench, project => { ShowProjectDrawer(project); ResumeNoteClick(this, new RoutedEventArgs()); },
        () => ShowDevelopmentTools(), () => ShowProductivity()) { Owner = this };
        _developmentHub = window; window.Closed += (_, _) => _developmentHub = null; window.Show();
    }
}
