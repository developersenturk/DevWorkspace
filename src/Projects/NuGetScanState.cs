using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DevWorkspace;

internal sealed record NuGetScanSnapshot(DateTimeOffset FinishedAt, NuGetReport Report);

internal sealed class NuGetScanState(NuGetScanSnapshot? initial, Action<NuGetScanSnapshot> save,
    CancellationToken workspaceToken = default) : INotifyPropertyChanged
{
    private NuGetScanSnapshot? _latest = initial;
    private string? _progress;
    private string? _error;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanScan => _progress is null;
    internal bool LastAttemptFailed => _error is not null;
    public bool HasResult => _latest is not null;
    public NuGetReport? Report => _latest?.Report;
    public string Status => _progress ?? (_error is not null ? Loc.Text("L913F44A3EEC7") : ResultText);
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
        null => Loc.Text("LADA5F0684B79"),
        { Diagnostics.Count: > 0 } report => Loc.Format("L635B37A91146", report.Findings.Count),
        { Findings.Count: > 0 } report => Loc.Format("LD9A5E14C5B34", report.Findings.Count),
        _ => Loc.Text("L39E33042373A")
    };

    public string LastScan => _latest is null ? "" : Loc.Format("L850BAB5AC443", _latest.FinishedAt.ToLocalTime());

    public string Breakdown => Report is not { Findings.Count: > 0 } report ? "" : string.Join(" · ",
        new[] { "Kritik", "Yüksek", "Orta", "Düşük", "Bilinmiyor" }
            .Select(severity => (Severity: severity, Count: report.Findings.Count(finding => finding.Severity == severity)))
            .Where(item => item.Count > 0).Select(item => $"{item.Count:N0} {Loc.Label(item.Severity).ToLower(CultureInfo.CurrentCulture)}"));

    public string Details => _error ?? (Report is { } report ? string.Join(Environment.NewLine, report.Diagnostics)
        : Loc.Text("L309DA6D75C0D"));

    public string ShortDetails => Shorten(Details, 500);
    public string Notice => _error is not null ? Shorten(_error, 160) : "";

    public async Task ScanAsync(ProjectEntry project, CancellationToken token)
    {
        if (!CanScan || token.IsCancellationRequested)
            return;
        _error = null;
        _progress = Loc.Text("LE4937ED2653D");
        var operation = OperationCenter.Start(Loc.Text("L6A8DDDA55E3B"), project.Name, () => _ = ScanAsync(project, workspaceToken));
        NotifyChanged();
        try
        {
            var progress = new Progress<string>(status =>
            {
                if (_progress is not null && !token.IsCancellationRequested)
                {
                    _progress = status;
                    operation.Append(status + Environment.NewLine);
                    NotifyChanged();
                }
            });
            var report = await ProjectIntegrations.ScanNuGetAsync(project, token, progress);
            if (!token.IsCancellationRequested)
            {
                _latest = new(DateTimeOffset.UtcNow, report);
                save(_latest);
                operation.Finish(Loc.Text("L7C6F3F5F37BC"), Loc.Format("L41A8C91CFFC6", report.Findings.Count) +
                    (report.Diagnostics.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, report.Diagnostics) : ""));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or JsonException or InvalidOperationException)
        {
            _error = ex is JsonException or InvalidOperationException ? Loc.Text("L78BA0D9AB12B") : ex.Message;
            operation.Finish(Loc.Text("LF0873175A881"), _error);
        }
        finally
        {
            if (operation.Status == Loc.Text("LAF7DA0BB4DFC"))
                operation.Finish(Loc.Text("L7C925A8624F1"));
            _progress = null;
            NotifyChanged();
        }
    }

    private static string Shorten(string text, int length) => text.Length > length ? text[..length] + "…" : text;

    private void NotifyChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
