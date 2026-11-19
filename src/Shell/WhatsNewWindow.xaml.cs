using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Core;

namespace DevWorkspace;

public partial class WhatsNewWindow : ThemedWindow
{
    private readonly AppReleaseNotes _notes;
    private readonly CancellationTokenSource _closed = new();
    private bool _loading;
    internal WhatsNewWindow(AppReleaseNotes notes)
    {
        _notes = notes;
        InitializeComponent();
        InstalledVersion.Text = AppUpdateText.Format("UpdateNotesInstalled", AppReleaseNotes.CurrentVersion);
        LoadStatus.Text = AppUpdateText.Text("UpdateNotesLocal");
        SetReleases(notes.Local(), AppReleaseNotes.CurrentVersion);
    }
    private void SetReleases(IReadOnlyList<AppReleaseNote> releases, string? selected)
    {
        ReleaseList.ItemsSource = releases;
        ReleaseList.SelectedItem = releases.FirstOrDefault(r => r.Version == selected) ?? releases.FirstOrDefault();
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WindowLoaded;
        ReleaseList.Focus();
        await RefreshAsync();
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        LoadStatus.Text = AppUpdateText.Text("UpdateNotesLoading");
        try
        {
            var releases = await _notes.RefreshAsync(_closed.Token);
            if (_closed.IsCancellationRequested) return;
            SetReleases(releases, (ReleaseList.SelectedItem as AppReleaseNote)?.Version);
            LoadStatus.Text = AppUpdateText.Text("UpdateNotesLoaded");
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            if (!_closed.IsCancellationRequested) LoadStatus.Text = AppUpdateText.Text("UpdateNotesOffline");
        }
        finally
        {
            _loading = false;
            if (!_closed.IsCancellationRequested) RefreshButton.IsEnabled = true;
        }
    }
    private void ReleaseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReleaseList.SelectedItem is not AppReleaseNote release) return;
        ReleaseTitle.Text = release.Title;
        ReleaseDetail.Text = string.Join(" · ", new[]
        {
            release.Label,
            release.Version == AppReleaseNotes.CurrentVersion ? AppUpdateText.Text("UpdateNotesCurrent") : null,
            release.Published?.LocalDateTime.ToString("d")
        }.Where(s => s is not null));
        NotesReader.Document = DocumentPreview.Create(string.IsNullOrWhiteSpace(release.Body)
            ? AppUpdateText.Text("UpdateNotesEmpty") : release.Body, markdown: true);
        NotesReader.Document.PagePadding = new Thickness(0, 0, 8, 12);
        NotesReader.Document.TextAlignment = TextAlignment.Left;
    }
    private void OpenReleaseClick(object sender, RoutedEventArgs e)
    {
        string url = VelopackUpdateFeed.RepositoryUrl + "/releases";
        if (ReleaseList.SelectedItem is AppReleaseNote { Published: not null } release)
            url += "/tag/v" + Uri.EscapeDataString(release.Version);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { LoadStatus.Text = url; }
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowClosed(object? sender, EventArgs e) => _closed.Cancel();
}
