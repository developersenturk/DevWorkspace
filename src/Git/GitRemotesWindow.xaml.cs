using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class GitRemotesWindow : ThemedWindow
{
    private readonly string? _trackingRemote;

    internal GitRemote? RemoteToRemove { get; private set; }

    internal GitRemotesWindow(string project, GitRemote[] remotes, string? trackingRemote)
    {
        _trackingRemote = trackingRemote;
        InitializeComponent();
        ProjectHeading.Text = project;
        RemoteList.ItemsSource = remotes;
    }

    private void RemoteSelectionChanged(object sender, RoutedEventArgs e) =>
        RemoveButton.IsEnabled = RemoteList.SelectedItem is GitRemote;

    private void RemoveClick(object sender, RoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is not GitRemote remote)
            return;

        string address = remote.Url is null ? "" : $"\n{remote.Url}";
        string tracking = _trackingRemote == remote.Name ? "\nEtkin dalın bu depoyla olan takibi de kaldırılacak." : "";
        if (MessageBox.Show(this, $"{remote.Name}{address}\n\nBu uzak depo bağlantısı yerel projeden kaldırılsın mı?{tracking}",
            "Bağlantıyı kaldır", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        RemoteToRemove = remote;
        DialogResult = true;
    }
}
