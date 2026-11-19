using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private void DevelopmentToolsClick(object sender, RoutedEventArgs e) => ShowDevelopmentTools();

    private void UpdateScanToolNotice()
    {
        var missing = new List<string>();
        if (_projects.Any(project => project.Repository is not null) && DevelopmentTools.Find(DevelopmentTool.Git) is null)
            missing.Add("Git");
        if (_projects.Any(project => ProjectDetection.IsJavaScript(project.EntryFile)) && DevelopmentTools.Find(DevelopmentTool.Node) is null)
            missing.Add("Node.js");
        if (_projects.Any(project => project.Kind == "Flutter / Dart") && DevelopmentTools.Find(DevelopmentTool.Flutter) is null)
            missing.Add("Flutter");
        if (_projects.Any(project => project.EntryFile is { } file && ProjectTools.IsDotNetEntry(file)) &&
            DevelopmentTools.Find(DevelopmentTool.DotNet) is null)
            missing.Add(".NET SDK");
        ScanSummaryTools.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanSummaryTools.Content = missing.Count == 1 ? $"{missing[0]} eksik · Kurulum" : Loc.Format("LF92438F249C3", missing.Count);
        ScanSummaryTools.ToolTip = string.Join(", ", missing);
    }

    private void ShowDevelopmentTools(DevelopmentTool? requested = null)
    {
        bool hadGit = DevelopmentTools.Find(DevelopmentTool.Git) is not null;
        new ToolSetupWindow(requested) { Owner = this }.ShowDialog();
        if (!hadGit && DevelopmentTools.Find(DevelopmentTool.Git) is not null)
            ScheduleWorkspaceRefresh();
        ApplyFilter();
    }

    private bool EnsureTool(DevelopmentTool tool)
    {
        if (DevelopmentTools.Find(tool) is not null)
            return true;
        ShowDevelopmentTools(tool);
        return DevelopmentTools.Find(tool) is not null;
    }

    private async Task<bool> EnsureSdkAsync()
    {
        try
        {
            bool ready = await ToolSetupWindow.EnsureReadyAsync(this, DevelopmentTool.DotNet, _closing.Token);
            if (!ready)
                StatusText.Text = Loc.Text("L8699C32DD91A");
            return ready;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.Format("L417F44E5B1BD", ex.Message);
            return false;
        }
    }

    private bool OfferProjectEditor(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder is null || MessageBox.Show(this,
                Loc.Text("LB09AA67DF2CC"),
                Loc.Text("LC919B8148D2D"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        if (!EnsureTool(DevelopmentTool.Editor))
            return false;
        try
        {
            var info = new ProcessStartInfo(DevelopmentTools.Require(DevelopmentTool.Editor)) { UseShellExecute = true };
            info.ArgumentList.Add(folder);
            Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            StatusText.Text = Loc.Format("L10455CFBEB8C", ex.Message);
            return false;
        }
    }
}
