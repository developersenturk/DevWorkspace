using System.Windows;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class GitRemoteBranchWindow : ThemedWindow
{
    internal GitRemoteBranchWindow(string[] remotes, string? selectedRemote, string currentBranch)
    {
        InitializeComponent();
        Description.Text = Loc.Format("L35E0DAB74AEE", currentBranch);
        RemoteInput.ItemsSource = remotes;
        RemoteInput.SelectedItem = remotes.FirstOrDefault(remote => remote == selectedRemote)
            ?? (remotes.Length == 1 ? remotes[0] : null);
        Loaded += (_, _) => (RemoteInput.SelectedItem is null ? (FrameworkElement)RemoteInput : BranchInput).Focus();
    }

    internal string Remote => (string)RemoteInput.SelectedItem;
    internal string Branch => BranchInput.Text.Trim();

    private void InputChanged(object sender, RoutedEventArgs e)
    {
        if (CreateButton is not null)
            CreateButton.IsEnabled = RemoteInput.SelectedItem is string && Branch.Length > 0;
    }

    private void CreateClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
