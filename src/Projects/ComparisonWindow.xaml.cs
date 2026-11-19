using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class ComparisonWindow : ThemedWindow
{
    private readonly Workspace _workspace;
    private readonly ProjectEntry _left;
    private readonly ProjectEntry _right;
    private readonly CancellationTokenSource _closing = new();
    private IReadOnlyList<FileDifference> _differences = [];
    private CancellationTokenSource? _preview;

    internal ComparisonWindow(Workspace workspace, ProjectEntry left, ProjectEntry right)
    {
        InitializeComponent();
        _workspace = workspace;
        _left = left;
        _right = right;
        LeftName.Text = Loc.Format("L1CE1D6431325", left.Name);
        RightName.Text = Loc.Format("L08668719AC61", right.Name);
        LeftPathText.Text = left.RelativePath;
        RightPathText.Text = right.RelativePath;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await Task.Run(() => ProjectComparison.Compare(_left.Folder, _right.Folder, _workspace.Config.ScanExclusions, _closing.Token));
            if (_closing.IsCancellationRequested)
                return;

            _differences = result.Differences;
            SummaryText.Text = Loc.Format("L3C75734A6EBE", result.Identical, _differences.Count(d => d.Kind == FileDifferenceKind.Modified)) +
                Loc.Format("L230EEBAAD0ED", _differences.Count(d => d.Kind == FileDifferenceKind.LeftOnly), _differences.Count(d => d.Kind == FileDifferenceKind.RightOnly));
            int skipped = result.Skipped + _differences.Count(d => d.Kind == FileDifferenceKind.Unreadable);
            if (skipped > 0)
                SummaryText.Text += Loc.Format("L38197D42FA04", skipped);
            FilterFiles();
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
    }

    private void WindowClosed(object? sender, EventArgs e) => _closing.Cancel();

    private void FileQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (DifferenceList is not null)
            FilterFiles();
    }

    private void FilterFiles()
    {
        string query = FileQuery.Text.Trim();
        var files = _differences.Where(file => file.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        DifferenceList.ItemsSource = files;
        DifferenceList.SelectedIndex = files.Length > 0 ? 0 : -1;
        if (files.Length == 0)
            PreviewCaption.Text = query.Length == 0 ? Loc.Text("L65426D628640") : Loc.Text("L508568E67CF7");
    }

    private async void DifferenceChanged(object sender, RoutedEventArgs e)
    {
        _preview?.Cancel();
        LinesGrid.ItemsSource = null;
        if (DifferenceList.SelectedItem is not FileDifference difference)
            return;

        using var preview = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _preview = preview;
        PreviewCaption.Text = $"{difference.Path}  ·  Metin okunuyor…";
        try
        {
            var result = await Task.Run(() => ProjectComparison.Preview(difference, preview.Token));
            if (!preview.IsCancellationRequested)
            {
                LinesGrid.ItemsSource = result.Lines;
                PreviewCaption.Text = string.IsNullOrWhiteSpace(result.Notice) ? $"{difference.Path}  ·  {difference.Label}" :
                    $"{difference.Path}  ·  {result.Notice}";
            }
        }
        catch (OperationCanceledException) when (preview.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_preview, preview))
                _preview = null;
        }
    }
}
