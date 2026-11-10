using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DevWorkspace;

internal sealed record NuGetScanSnapshot(DateTimeOffset FinishedAt, NuGetReport Report);

internal sealed class NuGetScanState(NuGetScanSnapshot? initial, Action<NuGetScanSnapshot> save) : INotifyPropertyChanged
{
    private NuGetScanSnapshot? _latest = initial;
    private string? _progress;
    private string? _error;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanScan => _progress is null;
    internal bool LastAttemptFailed => _error is not null;
    public bool HasResult => _latest is not null;
    public NuGetReport? Report => _latest?.Report;
    public string Status => _progress ?? (_error is not null ? "NuGet taraması tamamlanamadı." : ResultText);
    public string Tone => (_progress, _error, Report) switch
    {
        (not null, _, _) => "Neutral",
        (_, not null, _) => "Error",
        (_, _, null) => "Neutral",
        (_, _, { } report) when report.Findings.Any(finding => finding.Severity is "Kritik" or "Yüksek") => "Error",
        (_, _, { Findings.Count: > 0 } or { Diagnostics.Count: > 0 }) => "Warning",
        _ => "Success"
    };

    private string ResultText => Report switch
    {
        null => "Henüz taranmadı",
        { Diagnostics.Count: > 0 } report => $"{report.Findings.Count:N0} bulgu · Tarama uyarıları var",
        { Findings.Count: > 0 } report => $"{report.Findings.Count:N0} güvenlik bulgusu",
        _ => "Bilinen güvenlik açığı bulunamadı"
    };

    public string LastScan => _latest is null ? "" : $"Son tamamlanan tarama · {_latest.FinishedAt.ToLocalTime():g}";

    public string Breakdown => Report is not { Findings.Count: > 0 } report ? "" : string.Join(" · ",
        new[] { "Kritik", "Yüksek", "Orta", "Düşük", "Bilinmiyor" }
            .Select(severity => (Severity: severity, Count: report.Findings.Count(finding => finding.Severity == severity)))
            .Where(item => item.Count > 0).Select(item => $"{item.Count:N0} {item.Severity.ToLower(CultureInfo.GetCultureInfo("tr-TR"))}"));

    public string Details => _error ?? (Report is { } report ? string.Join(Environment.NewLine, report.Diagnostics)
        : "Paket bilgileri eksikse NuGet paketleri geri yüklenir ve tarama yeniden başlatılır.");

    public string ShortDetails => Shorten(Details, 500);
    public string Notice => _error is not null ? Shorten(_error, 160) : "";

    public async Task ScanAsync(ProjectEntry project, CancellationToken token)
    {
        if (!CanScan || token.IsCancellationRequested)
            return;
        _error = null;
        _progress = "NuGet paketleri taranıyor…";
        NotifyChanged();
        try
        {
            var progress = new Progress<string>(status =>
            {
                if (_progress is not null && !token.IsCancellationRequested)
                {
                    _progress = status;
                    NotifyChanged();
                }
            });
            var report = await ProjectIntegrations.ScanNuGetAsync(project, token, progress);
            if (!token.IsCancellationRequested)
            {
                _latest = new(DateTimeOffset.UtcNow, report);
                save(_latest);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or JsonException or InvalidOperationException)
        {
            _error = ex is JsonException or InvalidOperationException ? "NuGet taramasının sonucu okunamadı." : ex.Message;
        }
        finally
        {
            _progress = null;
            NotifyChanged();
        }
    }

    private static string Shorten(string text, int length) => text.Length > length ? text[..length] + "…" : text;

    private void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
