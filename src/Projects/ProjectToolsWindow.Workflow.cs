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
            ? "İş akışı dosyası oluşturuldu. İlk çalıştırma için bu dosyayı commit edip GitHub’a gönderin."
            : exists ? $"Bu depoda yerel bir iş akışı tanımı var. {Path.GetFileName(_workflowPath)}"
            : _remoteWorkflowExists ? "GitHub’da bu depo için zaten bir iş akışı tanımlı."
            : "GitHub’a kod gönderildiğinde seçilen proje veya çözüm derlenir ve kapsadığı testler çalışır.");
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
        UpdateWorkflowPanel("Proje ve SDK bilgileri okunuyor…");
        string? status = null;
        try
        {
            _workflowPlan = await Task.Run(() => DotNetWorkflow.Prepare(_project, _closing.Token), _closing.Token);
            if (_closing.IsCancellationRequested)
                return;
            WorkflowTargetText.Text = $"Derleme ve test hedefi · {_workflowPlan.Target}";
            WorkflowPreviewText.Text = _workflowPlan.Content;
            status = "Önizlemeyi inceleyip dosyayı oluşturabilirsiniz.";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (IsWorkflowError(ex)) { status = $"Önizleme hazırlanamadı. {ex.Message}"; }
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
        UpdateWorkflowPanel("İş akışı dosyası oluşturuluyor…");
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
            status = $"İş akışı dosyası oluşturulamadı. {ex.Message}";
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
