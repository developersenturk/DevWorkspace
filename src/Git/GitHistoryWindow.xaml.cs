using System.IO;
using System.Windows;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class GitHistoryWindow : ThemedWindow
{
    private readonly string _repository;
    private readonly CancellationTokenSource _closing = new();

    internal GitHistoryWindow(ProjectEntry project)
    {
        InitializeComponent();
        _repository = project.Repository ?? throw new ArgumentException(Loc.Text("L4B1E1C5AFA51"));
        HistoryTitle.Text = project.Name + Loc.Text("L66646057146E");
        RepositoryPath.Text = _repository;
    }

    private async void HistoryLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var commits = await GitProjectOperations.HistoryAsync(_repository, _closing.Token);
            if (_closing.IsCancellationRequested)
                return;
            HistoryGrid.ItemsSource = commits;
            HistoryStatus.Text = commits.Count == 0 ? Loc.Text("L1F13C04EE6C0") :
                Loc.Format("L27D1D06ECACC", commits.Count);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!_closing.IsCancellationRequested)
                HistoryStatus.Text = ex.Message;
        }
    }

    private void HistoryClosed(object? sender, EventArgs e) => _closing.Cancel();
}
