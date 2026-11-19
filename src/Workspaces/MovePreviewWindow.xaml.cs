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
        PlanStatus.Text = Loc.Text("L578EE47AD700");
    }

    private void WindowClosed(object? sender, EventArgs e) => _closing.Cancel();

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Loc.Text("LA08BA414AE16"), InitialDirectory = _workspace.Root };
        if (picker.ShowDialog(this) == true)
            TargetFolder.Text = picker.FolderName;
    }

    private void TargetChanged(object sender, TextChangedEventArgs e)
    {
        if (FindingsGrid is null)
            return;

        FindingsGrid.ItemsSource = null;
        DestinationText.Text = "";
        PlanStatus.Text = Loc.Text("L8121661BDB3F");
    }

    private async void AnalyzeClick(object sender, RoutedEventArgs e)
    {
        string parent = TargetFolder.Text.Trim();
        TargetFolder.IsEnabled = BrowseTargetButton.IsEnabled = AnalyzeButton.IsEnabled = false;
        PlanStatus.Text = Loc.Text("LFF5B2D60F4DB");
        try
        {
            var plan = await Task.Run(() => ProjectMovePlanner.Analyze(_workspace, _project, parent, _closing.Token));
            if (_closing.IsCancellationRequested)
                return;

            DestinationText.Text = Loc.Format("L8574966F2299", plan.Destination);
            FindingsGrid.ItemsSource = plan.Findings;
            PlanStatus.Text = Loc.Format("L083F3878A6B8", plan.FilesChecked, plan.Findings.Count) +
                (plan.Unresolved > 0 ? Loc.Format("L3951FA21B42A", plan.Unresolved) : "") +
                (plan.Skipped > 0 ? Loc.Format("L39ACF9CECFDE", plan.Skipped) : "") +
                Loc.Text("L0C51119B3056");
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
