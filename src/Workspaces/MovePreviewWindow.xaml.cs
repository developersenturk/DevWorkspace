using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class MovePreviewWindow : ThemedWindow
{
    private readonly Workspace _workspace;
    private readonly ProjectEntry _project;
    private readonly CancellationTokenSource _closing = new();

    internal MovePreviewWindow(Workspace workspace, ProjectEntry project)
    {
        InitializeComponent();
        _workspace = workspace;
        _project = project;
        ProjectTitle.Text = project.Name;
        SourcePath.Text = project.Folder;
        TargetFolder.Text = Path.GetDirectoryName(project.Folder) ?? workspace.Root;
        PlanStatus.Text = "Çalışma alanında başka bir üst klasör seçin. Bu ekran bir taşıma önizlemesidir.";
    }

    private void WindowClosed(object? sender, EventArgs e) => _closing.Cancel();

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Hedef üst klasörü seçin", InitialDirectory = _workspace.Root };
        if (picker.ShowDialog(this) == true)
            TargetFolder.Text = picker.FolderName;
    }

    private void TargetChanged(object sender, TextChangedEventArgs e)
    {
        if (FindingsGrid is null)
            return;

        FindingsGrid.ItemsSource = null;
        DestinationText.Text = "";
        PlanStatus.Text = "Yeni hedef için önizlemeyi çalıştırın.";
    }

    private async void AnalyzeClick(object sender, RoutedEventArgs e)
    {
        string parent = TargetFolder.Text.Trim();
        TargetFolder.IsEnabled = BrowseTargetButton.IsEnabled = AnalyzeButton.IsEnabled = false;
        PlanStatus.Text = "Başvurular ve hedef konum kontrol ediliyor…";
        try
        {
            var plan = await Task.Run(() => ProjectMovePlanner.Analyze(_workspace, _project, parent, _closing.Token));
            if (_closing.IsCancellationRequested)
                return;

            DestinationText.Text = $"Yeni konum · {plan.Destination}";
            FindingsGrid.ItemsSource = plan.Findings;
            PlanStatus.Text = $"{plan.FilesChecked:N0} yapılandırma dosyası incelendi · {plan.Findings.Count:N0} bulgu" +
                (plan.Unresolved > 0 ? $" · {plan.Unresolved:N0} dinamik başvuru" : "") +
                (plan.Skipped > 0 ? $" · {plan.Skipped:N0} atlanan öğe" : "") +
                " · Önizleme";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            PlanStatus.Text = ex.Message;
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
                TargetFolder.IsEnabled = BrowseTargetButton.IsEnabled = AnalyzeButton.IsEnabled = true;
        }
    }
}
