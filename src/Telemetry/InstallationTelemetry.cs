using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace DevWorkspace;

internal sealed class TelemetryState
{
    public bool? Enabled { get; set; }
    public Guid InstallationId { get; set; }
    public DateTimeOffset? LastSentUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? CachedEndpoint { get; set; }
    public DateTimeOffset? ConfigCheckedUtc { get; set; }
}

// No provider SDKs or credentials: the client only knows the versioned HTTPS contract.
internal sealed class InstallationTelemetry(HttpClient http, string statePath, string configUrl)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static InstallationTelemetry Current { get; } = new(
        new HttpClient { Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 16 * 1024 },
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevWorkspace", "telemetry.json"),
        "https://raw.githubusercontent.com/developersenturk/DevWorkspace/master/config/telemetry.json");

    internal TelemetryState Load()
    {
        try { return JsonSerializer.Deserialize<TelemetryState>(File.ReadAllText(statePath), Json) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private void Save(TelemetryState state)
    {
        string temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
            File.Move(temporary, statePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal bool SetEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            // Shared with heartbeat across running application instances.
            using var lease = new FileStream(statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var state = Load();
            state.Enabled = enabled;
            Save(state);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool IsSecureEndpoint(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.Query) && uri.AbsolutePath.EndsWith("/v1/heartbeat", StringComparison.Ordinal);

    internal async Task SendAsync(DateTimeOffset now, CancellationToken token = default)
    {
        try
        {
            // A declined/unanswered preference makes no HTTP requests, including config requests.
            if (Load().Enabled != true) return;
            using var lease = new FileStream(statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var state = Load();
            if (state.Enabled != true || state.LastSentUtc?.UtcDateTime.Date == now.UtcDateTime.Date ||
                (state.LastAttemptUtc is { } attempted && now - attempted < TimeSpan.FromHours(1))) return;
            state.LastAttemptUtc = now;
            if (state.InstallationId == Guid.Empty) state.InstallationId = Guid.NewGuid();
            Save(state);

            string? endpoint = Environment.GetEnvironmentVariable("DEVWORKSPACE_TELEMETRY_URL");
            if (endpoint is null)
            {
                string localConfig = Path.Combine(AppContext.BaseDirectory, "telemetry.config.json");
                if (File.Exists(localConfig))
                {
                    var local = JsonSerializer.Deserialize<EndpointConfig>(File.ReadAllText(localConfig), Json);
                    if (local is not { Protocol: 1, Enabled: true }) return;
                    endpoint = local.Endpoint;
                }
                else
                {
                    if (state.ConfigCheckedUtc is not { } checkedAt || now - checkedAt >= TimeSpan.FromDays(1))
                    {
                        try
                        {
                            var config = await http.GetFromJsonAsync<EndpointConfig>(configUrl, Json, token).ConfigureAwait(false);
                            if (config is { Protocol: 1, Enabled: false }) state.CachedEndpoint = null;
                            else if (config is { Protocol: 1, Enabled: true } && IsSecureEndpoint(config.Endpoint)) state.CachedEndpoint = config.Endpoint;
                            else return; // Unsupported/invalid config fails closed.
                            state.ConfigCheckedUtc = now;
                            Save(state);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                        {
                            // Only use a recently verified endpoint during transient config outages.
                            if (state.ConfigCheckedUtc is not { } cachedAt || now - cachedAt > TimeSpan.FromDays(7)) return;
                        }
                    }
                    endpoint = state.CachedEndpoint;
                }
            }
            if (!IsSecureEndpoint(endpoint)) return;
            string version = typeof(InstallationTelemetry).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
            using var response = await http.PostAsJsonAsync(endpoint, new { protocol = 1,
                installationId = state.InstallationId.ToString("D"), version }, Json, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            state.LastSentUtc = now;
            Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            // Optional statistics must never affect startup or the user's work.
        }
    }

    private sealed record EndpointConfig(int Protocol, bool Enabled, string? Endpoint);
}
