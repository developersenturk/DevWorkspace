using System.Windows;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class GitAmendWindow : ThemedWindow
{
    internal string Message => MessageInput.Text;

    internal GitAmendWindow(string project, string commitId, string message, int stagedFiles)
    {
        InitializeComponent();
        CommitInfo.Text = $"{project} · {commitId[..Math.Min(8, commitId.Length)]}";
        StagedInfo.Text = stagedFiles == 0 ? Loc.Text("AmendMessageOnly") : Loc.Format("AmendStaged", stagedFiles);
        MessageInput.Text = message;
        ApplyButton.IsEnabled = !string.IsNullOrWhiteSpace(message);
        Loaded += (_, _) => MessageInput.Focus();
    }

    private void MessageChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ApplyButton is not null) ApplyButton.IsEnabled = !string.IsNullOrWhiteSpace(Message);
    }

    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(Message)) DialogResult = true;
    }
}
