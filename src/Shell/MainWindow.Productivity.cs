using System.IO;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private ProductivityWindow? _productivityWindow;
    private bool _restoringWorkSet;
    private void ProductivityClick(object sender, RoutedEventArgs e) => ShowProductivity();

    private void ShowProductivity(int tab = 0)
    {
        if (_busy || _workspace is not { } workspace) return;
        if (_productivityWindow is { } existing)
        { existing.SelectTab(tab); existing.WindowState = WindowState.Normal; existing.Activate(); return; }
        var window = new ProductivityWindow(workspace, () => _projects.ToArray(), () => _session.For(workspace.Root), state =>
        {
            var previous = _session.For(workspace.Root); _session.Workspaces[workspace.Root] = state;
            if (_session.SaveTo(SessionState.FilePath)) return true;
            _session.Workspaces[workspace.Root] = previous; return false;
        }, RestoreWorkSetAsync, profile => RunDevelopmentProfileAsync(profile), name => _developmentHub?.SelectProfile(name),
        ShowWorkspaceTools, tool => ShowDevelopmentTools(tool), _closing.Token, tab) { Owner = this };
        _productivityWindow = window; window.Closed += (_, _) => _productivityWindow = null; window.Show();
    }

    private async Task RestoreWorkSetAsync(WorkSet work, bool startServices, CancellationToken token)
    {
        if (_restoringWorkSet || _startingProfile || _busy || _gitView?.IsBusy == true || _workspace is not { } workspace)
            throw new IOException(WorkbenchText.T("AlreadyRunning"));
        var profile = (_session.For(workspace.Root).Profiles ?? []).FirstOrDefault(p => p.Name == work.Profile);
        if (startServices && profile is null) throw new IOException(WorkbenchText.T("WorkSetProfileMissing"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _closing.Token);
        _restoringWorkSet = true; SetBusy(true);
        try
        {
            var runs = _developmentRuns.Where(p => p.Key.StartsWith(workspace.Root + "\n", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToArray();
            foreach (var run in runs) run.Stop();
            await Task.WhenAll(runs.Select(r => r.StopCompletion));
            await WorkSets.RestoreBranchesAsync(work, _projects.ToArray(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var previous = _session.For(workspace.Root);
            var notes = new Dictionary<string, ResumeNote>(previous.ResumeNotes ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var project in work.Projects)
            { notes.Remove(project.Project); if (project.Note is not null) notes[project.Project] = project.Note; }
            _session.Workspaces[workspace.Root] = previous with { ResumeNotes = notes, LastProfile = work.Profile ?? previous.LastProfile, LastWorkSet = work.Id };
            if (!_session.SaveTo(SessionState.FilePath))
            { _session.Workspaces[workspace.Root] = previous; throw new IOException(WorkbenchText.T("WorkSetPersistFailed")); }
            SetBusy(false); await RefreshAsync();
            foreach (var saved in work.Projects)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_workspace?.Root != workspace.Root) throw new OperationCanceledException();
                if (_projects.FirstOrDefault(p => p.RelativePath.Equals(saved.Project, StringComparison.OrdinalIgnoreCase)) is { } project)
                { FocusProject(project); await OpenProjectAsync(project); }
            }
            _developmentHub?.SelectProfile(work.Profile ?? "");
        }
        finally { _restoringWorkSet = false; if (!_closing.IsCancellationRequested) SetBusy(false); }
        cancellation.Token.ThrowIfCancellationRequested();
        if (startServices && profile is not null) await RunDevelopmentProfileAsync(profile);
    }
}
