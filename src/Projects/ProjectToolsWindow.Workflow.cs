using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Xml;

namespace DevWorkspace;

public partial class ProjectToolsWindow
{
    private bool _remoteWorkflowExists;
    private bool _workingOnWorkflow;
    private bool _workflowCreated;
    private string? _workflowPath;
    private DotNetWorkflowPlan? _workflowPlan;

    internal bool OpenGitAfterClose { get; private set; }

    private void UpdateWorkflowPanel(string? message = null)
    {
        if (!DotNetWorkflow.Supports(_project))
            return;
        WorkflowPanel.Visibility = Visibility.Visible;
        bool inspectFailed = false;
        try { _workflowPath = DotNetWorkflow.ExistingWorkflow(_project); }
        catch (Exception ex) when (IsWorkflowError(ex))
        {
            inspectFailed = true;
            _workflowPath = null;
            message ??= ex.Message;
        }

        bool exists = _workflowPath is not null;
        bool canCreate = !exists && !_remoteWorkflowExists && !inspectFailed;
        if (!canCreate)
            _workflowPlan = null;
        WorkflowStatus.Text = message ?? (_workflowCreated && exists
            ? Loc.Text("L9AC46EF6719E")
            : exists ? Loc.Format("L5E225B161B94", Path.GetFileName(_workflowPath))
            : _remoteWorkflowExists ? Loc.Text("L92C31D6F250B")
            : Loc.Text("L045FE28C2116"));
        WorkflowPreviewButton.Visibility = canCreate && _workflowPlan is null ? Visibility.Visible : Visibility.Collapsed;
        WorkflowPreviewButton.IsEnabled = !_workingOnWorkflow;
        WorkflowFileButton.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        WorkflowGitButton.Visibility = exists && _workflowCreated ? Visibility.Visible : Visibility.Collapsed;
        WorkflowPreviewPanel.Visibility = _workflowPlan is null ? Visibility.Collapsed : Visibility.Visible;
        WorkflowCreateButton.IsEnabled = !_workingOnWorkflow && canCreate && ActionsRefreshButton.IsEnabled;
    }

    private async void WorkflowPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_workingOnWorkflow || _remoteWorkflowExists)
            return;
        _workingOnWorkflow = true;
        UpdateWorkflowPanel(Loc.Text("LDE9144DF44CA"));
        string? status = null;
        try
        {
            _workflowPlan = await Task.Run(() => DotNetWorkflow.Prepare(_project, _closing.Token), _closing.Token);
            if (_closing.IsCancellationRequested)
                return;
            WorkflowTargetText.Text = Loc.Format("L3813B444BBCB", _workflowPlan.Target);
            WorkflowPreviewText.Text = _workflowPlan.Content;
            status = Loc.Text("LD90A513D501A");
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (IsWorkflowError(ex)) { status = Loc.Format("LDD8AF60158B9", ex.Message); }
        finally
        {
            _workingOnWorkflow = false;
            if (!_closing.IsCancellationRequested)
                UpdateWorkflowPanel(status);
        }
    }

    private async void WorkflowCreateClick(object sender, RoutedEventArgs e)
    {
        if (_workingOnWorkflow || !ActionsRefreshButton.IsEnabled || _remoteWorkflowExists || _workflowPlan is not { } plan)
            return;
        _workingOnWorkflow = true;
        UpdateWorkflowPanel(Loc.Text("LC259A4F2A01E"));
        string? status = null;
        try
        {
            await Task.Run(() => DotNetWorkflow.Create(_project, plan, _closing.Token), _closing.Token);
            _workflowCreated = true;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (IsWorkflowError(ex))
        {
            _workflowPlan = null;
            status = Loc.Format("LF6DDAE192847", ex.Message);
        }
        finally
        {
            _workingOnWorkflow = false;
            if (!_closing.IsCancellationRequested)
                UpdateWorkflowPanel(status);
        }
    }

    private void WorkflowFileClick(object sender, RoutedEventArgs e)
    {
        if (_workflowPath is not { } path || !File.Exists(path))
        {
            UpdateWorkflowPanel();
            return;
        }
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            info.ArgumentList.Add(path);
            Process.Start(info);
        }
        catch (Win32Exception ex) { WorkflowStatus.Text = ex.Message; }
    }

    private void WorkflowGitClick(object sender, RoutedEventArgs e)
    {
        OpenGitAfterClose = true;
        Close();
    }

    private static bool IsWorkflowError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or XmlException or ArgumentException or InvalidOperationException;
}
