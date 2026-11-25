using System.IO;

namespace DevWorkspace;

internal static class DevelopmentPreflight
{
    internal static bool Blocks(DevelopmentCheckRow row) => row.Category == Loc.Text("LE8AF1BA5B6FF") ||
        row.Category == Loc.Text("L3B5AFEB0ED88") || (row.Name == ".NET SDK" && row.Category == Loc.Text("L3A1CCF19A70C")) ||
        row.Detail == WorkbenchText.T("ToolMissing") || ((row.Name is ".env" or "Node.js") && row.Category == WorkbenchText.T("Attention"));

    internal static async Task<DevelopmentCheckRow[]> ReadAsync(Workspace workspace, ProjectEntry[] projects,
        LaunchProfile? profile, string[] requiredTools, CancellationToken token)
    {
        if (profile is not null) DevelopmentWorkflow.Validate(profile);
        var selected = profile is null ? projects : profile.Steps.Select(s => projects.FirstOrDefault(p =>
            p.RelativePath.Equals(s.Project, StringComparison.OrdinalIgnoreCase)) ??
            throw new IOException(string.Format(WorkbenchText.T("MissingWorkflowProject"), s.Project)))
            .DistinctBy(p => p.Folder, StringComparer.OrdinalIgnoreCase).ToArray();
        using var limit = new SemaphoreSlim(4);
        var results = await Task.WhenAll(selected.Select(async p =>
        {
            await limit.WaitAsync(token);
            try
            {
                string? file = profile is null ? null : LaunchEnvironment.FileFor(profile, p);
                if (file is not null) _ = LaunchEnvironment.Read(file);
                var checks = await ProjectInspection.EnvironmentAsync(workspace, p, token, file);
                return checks.Select(r => new DevelopmentCheckRow(p.Name, r.Category, r.Name, r.Detail, p.Folder)).ToArray();
            }
            finally { limit.Release(); }
        }));
        var rows = results.SelectMany(r => r).ToList();
        foreach (var tool in requiredTools)
        {
            var path = FindTool(tool);
            rows.Add(new("—", WorkbenchText.T(path is null ? "Attention" : "Ready"), tool, path ?? WorkbenchText.T("ToolMissing")));
        }
        foreach (var step in profile?.Steps ?? [])
        {
            if (step.Command is { } command)
            {
                var tool = ProjectTools.RequiredTool(command);
                string? path = tool is { } known ? DevelopmentTools.Find(known) :
                    Path.IsPathFullyQualified(command.Executable) ? (File.Exists(command.Executable) ? command.Executable : null) : FindTool(command.Executable);
                if (path is null) rows.Add(new(step.Project, WorkbenchText.T("Attention"), command.Executable, WorkbenchText.T("ToolMissing")));
            }
            if (step.ReadyPort is int port)
            {
                bool busy = await DevelopmentWorkflow.ListeningAsync(port, token);
                rows.Add(new(step.Project, WorkbenchText.T(busy ? "Attention" : "Ready"), "TCP " + port,
                    busy ? string.Format(WorkbenchText.T("PortOccupied"), port, step.Project) : WorkbenchText.T("PortAvailable")));
            }
        }
        token.ThrowIfCancellationRequested(); return rows.ToArray();
    }

    internal static string? FindTool(string executable)
    {
        if (DevelopmentTools.Catalog.FirstOrDefault(t => t.Command.Equals(executable, StringComparison.OrdinalIgnoreCase)) is { } tool)
            return DevelopmentTools.Find(tool.Id);
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(Environment.SystemDirectory);
        return paths.Select(p => Path.Combine(Environment.ExpandEnvironmentVariables(p.Trim('"')), executable)).FirstOrDefault(File.Exists);
    }
}
