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
        _repository = project.Repository ?? throw new ArgumentException("Git deposu bulunamadı.");
        HistoryTitle.Text = project.Name + " · Git geçmişi";
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
            HistoryStatus.Text = commits.Count == 0 ? "Bu depoda henüz commit yok." :
                $"{commits.Count:N0} commit · Geçerli dalın en son 100 kaydı";
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
