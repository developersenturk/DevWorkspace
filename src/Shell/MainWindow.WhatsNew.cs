using System.IO;
using System.Net.Http;
using System.Windows;

namespace DevWorkspace;

public partial class MainWindow
{
    private static readonly HttpClient ReleaseNotesClient = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly AppReleaseNotes _releaseNotes = new(ReleaseNotesClient,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevWorkspace", "news"));
    private WhatsNewWindow? _whatsNew;
    private void InitializeWhatsNew()
    {
        NewsBadge.Visibility = _releaseNotes.HasUnread ? Visibility.Visible : Visibility.Collapsed;
        WhatsNewButton.ToolTip = AppUpdateText.Format("UpdateNotesInstalled", AppReleaseNotes.CurrentVersion);
    }
    private void WhatsNewClick(object sender, RoutedEventArgs e)
    {
        if (_whatsNew is not null) { _whatsNew.Activate(); return; }
        _whatsNew = new WhatsNewWindow(_releaseNotes) { Owner = this };
        _whatsNew.Closed += (_, _) => _whatsNew = null;
        _whatsNew.Show();
        _releaseNotes.MarkRead();
        NewsBadge.Visibility = Visibility.Collapsed;
    }
}
