using System.Net;
using System.Text;
using System.Text.Json;
using DevWorkspace;

string directory = Path.Combine(Path.GetTempPath(), "DevWorkspace-telemetry-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    int checks = 0;
    void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    var handler = new FakeHandler();
    using var http = new HttpClient(handler);
    var service = new InstallationTelemetry(http, Path.Combine(directory, "state.json"), "https://config.test/config.json");
    var date = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    await service.SendAsync(date);
    Check(handler.Requests.Count == 0, "No traffic before consent");
    Check(service.SetEnabled(false), "Preference persisted");
    await service.SendAsync(date);
    Check(handler.Requests.Count == 0, "No traffic after decline");
    Check(service.SetEnabled(true), "Can opt in");
    await service.SendAsync(date);
    Check(handler.Requests.Count == 2, "Fetch config then send");
    string firstPayload = handler.Requests.Last().Body!;
    using var payload = JsonDocument.Parse(firstPayload);
    Check(payload.RootElement.EnumerateObject().Count() == 3, "Only allowed fields sent");
    Check(Guid.TryParse(payload.RootElement.GetProperty("installationId").GetString(), out var id) && id != Guid.Empty, "Random ID");
    await service.SendAsync(date.AddHours(2));
    Check(handler.Requests.Count == 2, "Once per UTC day");
    await service.SendAsync(date.AddDays(1));
    Check(handler.Requests.Last().Body == firstPayload, "Stable ID across days");
    handler.Fail = true;
    int before = handler.Requests.Count;
    await service.SendAsync(date.AddDays(2));
    Check(service.Load().LastSentUtc == date.AddDays(1), "Failure does not mark sent");
    await service.SendAsync(date.AddDays(2).AddMinutes(1));
    Check(handler.Requests.Count == before + 2, "Failed attempts limited to hourly");
    handler.Fail = false;
    await service.SendAsync(date.AddDays(2).AddHours(1));
    Check(service.Load().LastSentUtc == date.AddDays(2).AddHours(1), "Retries recover");
    Check(service.SetEnabled(false), "Can opt out");
    before = handler.Requests.Count;
    await service.SendAsync(date.AddDays(3));
    Check(handler.Requests.Count == before, "Opt out stops traffic");
    Check(!InstallationTelemetry.IsSecureEndpoint("http://test/v1/heartbeat"), "Reject HTTP");
    Check(!InstallationTelemetry.IsSecureEndpoint("https://secret@test/v1/heartbeat"), "Reject credentials");
    Check(!InstallationTelemetry.IsSecureEndpoint("https://test/v1/heartbeat?token=secret"), "Reject query");
    Check(InstallationTelemetry.IsSecureEndpoint("https://other-provider.test/v1/heartbeat"), "Provider independent endpoint");
    Check(service.SetEnabled(true), "Re-enable");
    handler.Endpoint = "https://replacement.test/v1/heartbeat";
    await service.SendAsync(date.AddDays(4));
    Check(handler.Requests.Last().Url.Host == "replacement.test", "Remote config migrates existing clients");
    handler.Enabled = false;
    before = handler.Requests.Count;
    await service.SendAsync(date.AddDays(5));
    Check(handler.Requests.Count == before + 1, "Remote disable makes no heartbeat request");
    Console.WriteLine($"{checks} telemetry client checks passed.");
}
finally { Directory.Delete(directory, true); }

sealed class FakeHandler : HttpMessageHandler
{
    public List<(Uri Url, string? Body)> Requests { get; } = [];
    public bool Fail { get; set; }
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "https://collector.test/v1/heartbeat";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Add((request.RequestUri!, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
        if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { protocol = 1, enabled = Enabled, endpoint = Endpoint }), Encoding.UTF8, "application/json") };
        return new(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
    }
}
