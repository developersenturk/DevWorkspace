using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace DevWorkspace;

internal static class DevelopmentWorkflow
{
    internal static void Validate(LaunchProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 120 || profile.Steps is not { Length: > 0 and <= 100 })
            throw new InvalidDataException(WorkbenchText.T("InvalidWorkflow"));
        if (profile.Steps.Where(s => s?.ReadyPort is not null).Select(s => s.ReadyPort).Distinct().Count() !=
            profile.Steps.Count(s => s?.ReadyPort is not null)) throw new InvalidDataException(WorkbenchText.T("InvalidWorkflow"));
        if (profile.EnvironmentName?.Length > 120 || profile.EnvironmentFiles?.Any(p => !LaunchEnvironment.RelativePath(p.Value) ||
            !profile.Steps.Any(s => s is not null && s.Project.Equals(p.Key, StringComparison.OrdinalIgnoreCase))) == true)
            throw new InvalidDataException(WorkbenchText.T("EnvironmentPathRule"));
        foreach (var step in profile.Steps)
            if (step is null || string.IsNullOrWhiteSpace(step.Project) || step.Action is not ("default" or "terminal") ||
                step.ReadyPort is < 1 or > 65535 || step.TimeoutSeconds is < 1 or > 3600 ||
                (step.WaitForExit && step.ReadyPort is not null) ||
                ((step.WaitForExit || step.ReadyPort is not null) && step.Command is null))
                throw new InvalidDataException(WorkbenchText.T("InvalidWorkflow"));
            else if (step.Command is { } command && (string.IsNullOrWhiteSpace(command.Executable) || command.Arguments is null ||
                command.Arguments.Length > 16000 || (step.Action == "terminal")))
                throw new InvalidDataException(WorkbenchText.T("InvalidWorkflow"));
    }

    internal static async Task<bool> ListeningAsync(int port, CancellationToken token)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
        try { await client.ConnectAsync("127.0.0.1", port, timeout.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    internal static async Task AwaitReadyAsync(LaunchStep step, Task completion, Func<bool> failed,
        Action<string> progress, CancellationToken token, Func<CancellationToken, Task<bool>>? ownsPort = null)
    {
        if (!step.WaitForExit && step.ReadyPort is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
        try
        {
            if (step.WaitForExit)
            {
                await completion.WaitAsync(timeout.Token);
                if (failed()) throw new IOException(WorkbenchText.T("StepFailed"));
                return;
            }
            progress(string.Format(WorkbenchText.T("WaitingPort"), step.ReadyPort));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (completion.IsCompleted) throw new IOException(WorkbenchText.T("StepExited"));
                if (await ListeningAsync(step.ReadyPort!.Value, timeout.Token) &&
                    (ownsPort is null || await ownsPort(timeout.Token)))
                {
                    if (completion.IsCompleted) throw new IOException(WorkbenchText.T("StepExited"));
                    return;
                }
                await Task.Delay(200, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException(string.Format(WorkbenchText.T("StepTimeout"), step.TimeoutSeconds)); }
    }
}

internal sealed class DevelopmentRun : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly List<WorkspaceOperation> _operations = [];
    private readonly List<Task> _completions = [];
    private sealed record ComposeRun(string Folder, HashSet<string> Existing, Task Completion, IReadOnlyDictionary<string, string>? Environment);
    private readonly List<ComposeRun> _compose = [];
    private Task? _cleanup;
    internal DevelopmentRun(CancellationToken token) => _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
    internal CancellationToken Token => _cancellation.Token;
    internal bool Starting { get; private set; } = true;
    internal bool IsActive => Starting || _operations.Any(o => o.IsRunning) || _cleanup is { IsCompleted: false };
    internal Task StopCompletion => Task.WhenAll(_completions.Append(_cleanup ?? Task.CompletedTask));
    internal void Stop()
    {
        Starting = false; _cancellation.Cancel(); foreach (var op in _operations.ToArray()) op.Cancel?.Invoke();
        _cleanup ??= CleanupComposeAsync();
    }
    public void Dispose() { Stop(); _cancellation.Dispose(); }

    internal async Task StartAsync(LaunchProfile profile, IReadOnlyList<ProjectEntry> projects,
        Func<ProjectEntry, Task> open, Action<ProjectEntry> terminal, Action<string> progress)
    {
        try
        {
            DevelopmentWorkflow.Validate(profile);
            // Resolve the whole plan before starting any process.
            var plan = profile.Steps.Select(step => (Step: step, Project: projects.FirstOrDefault(p =>
                p.RelativePath.Equals(step.Project, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException(string.Format(WorkbenchText.T("MissingWorkflowProject"), step.Project)))).ToArray();
            foreach (var (step, project) in plan)
            {
                Token.ThrowIfCancellationRequested();
                if (!Directory.Exists(project.Folder)) throw new DirectoryNotFoundException(project.Folder);
                if (LaunchEnvironment.FileFor(profile, project) is { } environmentFile) _ = await Task.Run(() => LaunchEnvironment.Read(environmentFile), Token);
                if (step.ReadyPort is int port && await DevelopmentWorkflow.ListeningAsync(port, Token))
                    throw new IOException(string.Format(WorkbenchText.T("PortOccupied"), port, project.Name));
            }
            foreach (var (step, project) in plan)
            {
                Token.ThrowIfCancellationRequested();
                progress(project.Name + " · " + (step.Command?.Display ?? step.Action));
                if (step.Action == "terminal") terminal(project);
                else if (step.Command is { } command)
                {
                    if (step.ReadyPort is int readyPort && await DevelopmentWorkflow.ListeningAsync(readyPort, Token))
                        throw new IOException(string.Format(WorkbenchText.T("PortOccupied"), readyPort, project.Name));
                    if (OperationCenter.Entries.Any(o => o.IsRunning && o.Command == command &&
                        string.Equals(o.Folder, project.Folder, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException(WorkbenchText.T("AlreadyRunning") + " " + project.Name);
                    string? file = LaunchEnvironment.FileFor(profile, project);
                    var environment = file is null ? null : await Task.Run(() => LaunchEnvironment.Read(file), Token);
                    HashSet<string>? existingContainers = IsManagedCompose(command)
                        ? await ReadComposeContainersAsync(project.Folder, Token, environment) : null;
                    Token.ThrowIfCancellationRequested();
                    var execution = OperationCenter.StartCommand(command, project.Name, project.Folder, Token, file, environment);
                    _operations.Add(execution.Operation);
                    _completions.Add(execution.Completion);
                    if (existingContainers is not null) _compose.Add(new(project.Folder, existingContainers, execution.Completion, environment));
                    await DevelopmentWorkflow.AwaitReadyAsync(step, execution.Completion,
                        () => execution.Operation.IsFailed, progress, Token, async token =>
                        {
                            if (IsManagedCompose(command))
                            {
                                var info = ProjectTools.CreateCaptureInfo(DevelopmentTools.Require(DevelopmentTool.Docker), project.Folder,
                                    ["compose", "ps", "--format", "json"], environment);
                                var (code, output, error) = await ProjectTools.CaptureAsync(info, "Docker Compose", 10, token);
                                if (code != 0) throw new IOException(error);
                                return ComposeOwnsPort(output, step.ReadyPort!.Value);
                            }
                            await Task.Run(() => RuntimePorts.RefreshAsync([execution.Operation], token), token);
                            return execution.Operation.Ports.Split(',').Any(p => p.Trim().EndsWith(":" + step.ReadyPort, StringComparison.Ordinal));
                        });
                    if (execution.Operation.IsFailed) throw new IOException(WorkbenchText.T("StepFailed"));
                }
                else await open(project);
            }
        }
        catch { Stop(); throw; }
        finally { Starting = false; }
    }

    private static bool IsManagedCompose(ProjectCommand command) => command.Executable.Equals("docker.exe", StringComparison.OrdinalIgnoreCase) &&
        command.Arguments.Trim().Equals("compose up", StringComparison.OrdinalIgnoreCase);

    internal static bool ComposeOwnsPort(string output, int port)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        using var document = JsonDocument.Parse(output.TrimStart().StartsWith('[') ? output :
            "[" + string.Join(",", output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) + "]");
        return document.RootElement.EnumerateArray().Any(service =>
            service.TryGetProperty("State", out var state) && state.GetString() == "running" &&
            service.TryGetProperty("Publishers", out var publishers) && publishers.ValueKind == JsonValueKind.Array &&
            publishers.EnumerateArray().Any(p => p.TryGetProperty("PublishedPort", out var published) &&
                published.ValueKind == JsonValueKind.Number && published.TryGetInt32(out int actual) && actual == port &&
                p.TryGetProperty("Protocol", out var protocol) && protocol.GetString() == "tcp"));
    }

    private static async Task<HashSet<string>> ReadComposeContainersAsync(string folder, CancellationToken token, IReadOnlyDictionary<string, string>? environment)
    {
        var info = ProjectTools.CreateCaptureInfo(DevelopmentTools.Require(DevelopmentTool.Docker), folder,
            ["compose", "ps", "--status", "running", "--quiet"], environment);
        var (code, output, error) = await ProjectTools.CaptureAsync(info, "Docker Compose", 15, token);
        if (code != 0) throw new IOException(error);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-fA-F0-9]{12,64}$")).ToHashSet(StringComparer.Ordinal);
    }

    private async Task CleanupComposeAsync()
    {
        foreach (var compose in _compose.ToArray())
        {
            var operation = OperationCenter.Start(WorkbenchText.T("StopCompose"), compose.Folder);
            try
            {
                await compose.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                var running = await ReadComposeContainersAsync(compose.Folder, CancellationToken.None, compose.Environment);
                string[] owned = running.Except(compose.Existing, StringComparer.Ordinal).ToArray();
                if (owned.Length > 0)
                {
                    var info = ProjectTools.CreateCaptureInfo(DevelopmentTools.Require(DevelopmentTool.Docker), compose.Folder, ["stop", .. owned], compose.Environment);
                    var (code, output, error) = await ProjectTools.CaptureAsync(info, "Docker stop", 30, CancellationToken.None);
                    operation.Append(output + error);
                    if (code != 0) throw new IOException(error);
                }
                operation.Finish(Loc.Text("L7C6F3F5F37BC"));
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or TimeoutException or InvalidOperationException)
            { operation.Finish(Loc.Text("LF0873175A881"), ex.Message); }
        }
    }
}

internal sealed record DevelopmentConfiguration(int SchemaVersion, LaunchProfile[] Profiles, string[] RequiredTools);

internal static class DevelopmentConfigurationFile
{
    internal const string FileName = "devworkspace.development.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static string Export(LaunchProfile[] profiles, string root, string[]? requiredTools = null)
    {
        var config = new DevelopmentConfiguration(1, profiles, requiredTools ?? []);
        foreach (var profile in profiles) DevelopmentWorkflow.Validate(profile);
        // Store the root as a variable so the same commands work on another machine.
        string content = JsonSerializer.Serialize(config, Json);
        string escapedRoot = JsonSerializer.Serialize(Path.TrimEndingDirectorySeparator(root))[1..^1];
        content = content.Replace(escapedRoot, "${workspace}", StringComparison.OrdinalIgnoreCase);
        _ = Import(content, root); // Reject machine-specific project/executable paths before writing.
        return content;
    }

    internal static DevelopmentConfiguration Import(string content, string root)
    {
        if (content.Length > 1024 * 1024) throw new InvalidDataException(WorkbenchText.T("InvalidWorkflow"));
        var config = JsonSerializer.Deserialize<DevelopmentConfiguration>(content, Json);
        if (config is null || config.SchemaVersion != 1 || config.Profiles is null || config.Profiles.Length > 100 ||
            config.RequiredTools is null || config.RequiredTools.Length > 30 ||
            config.RequiredTools.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 80 || Path.GetFileName(t) != t || t.Contains(':')) ||
            config.Profiles.Select(p => p?.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Profiles.Length)
            throw new InvalidDataException(WorkbenchText.T("InvalidWorkflow"));
        foreach (var profile in config.Profiles)
        {
            if (profile is null) throw new InvalidDataException(WorkbenchText.T("InvalidWorkflow"));
            DevelopmentWorkflow.Validate(profile);
            foreach (var step in profile.Steps)
            {
                string path = step.Project.Replace('/', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(path) || path.Contains(':') || path.Split('\\', '/').Contains("..") ||
                    (step.Command is { } command && (string.IsNullOrWhiteSpace(command.Executable) ||
                    Path.GetFileName(command.Executable) != command.Executable || command.Executable.Contains(':') ||
                    System.Text.RegularExpressions.Regex.IsMatch(command.Arguments, @"[A-Za-z]:[\\/]|\\\\"))))
                    throw new InvalidDataException(WorkbenchText.T("PortableWorkflow"));
            }
        }
        return config with { Profiles = config.Profiles.Select(p => p with { EnvironmentFiles = p.EnvironmentFiles?.ToDictionary(
            pair => pair.Key.Replace('/', Path.DirectorySeparatorChar), pair => pair.Value.Replace('/', Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase),
            Steps = p.Steps.Select(s => s with {
            Project = s.Project.Replace('/', Path.DirectorySeparatorChar),
            Command = s.Command is { } c ? c with { Arguments = c.Arguments.Replace("${workspace}", root, StringComparison.Ordinal) } : null
        }).ToArray() }).ToArray() };
    }
}
