using System.Diagnostics;

namespace DevWorkspace;

internal sealed record ManagedResourceRow(string Project, string Command, int Pid, double? CpuPercent, double MemoryMb, int Processes);

internal sealed class ManagedResourceSampler
{
    private Dictionary<(int Pid, long Started), TimeSpan> _previous = [];
    private DateTime? _previousTime;

    internal ManagedResourceRow[] Read(WorkspaceOperation[] operations, CancellationToken token)
    {
        var parents = RuntimePorts.ProcessParents();
        var now = DateTime.UtcNow; var next = new Dictionary<(int, long), TimeSpan>();
        var rows = new List<ManagedResourceRow>(); var claimed = new HashSet<int>();
        foreach (var operation in operations.Where(o => o.IsRunning && o.ProcessId is not null))
        {
            token.ThrowIfCancellationRequested(); int root = operation.ProcessId!.Value;
            try
            {
                using var process = Process.GetProcessById(root);
                if (operation.ProcessStartedUtc != process.StartTime.ToUniversalTime()) continue;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            var owned = new HashSet<int> { root };
            bool changed;
            do { changed = false; foreach (var pair in parents) if (owned.Contains(pair.Value)) changed |= owned.Add(pair.Key); } while (changed);
            long memory = 0; double milliseconds = 0; int count = 0;
            foreach (int pid in owned)
            {
                token.ThrowIfCancellationRequested();
                if (!claimed.Add(pid)) continue;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var key = (pid, process.StartTime.ToUniversalTime().Ticks); var cpu = process.TotalProcessorTime;
                    next[key] = cpu; memory += process.WorkingSet64; count++;
                    if (_previous.TryGetValue(key, out var previous)) milliseconds += Math.Max(0, (cpu - previous).TotalMilliseconds);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            double? percent = _previousTime is { } last && now > last ? Math.Clamp(milliseconds / (now - last).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100) : null;
            rows.Add(new(operation.Target, operation.Name, root, percent, memory / 1048576d, count));
        }
        _previous = next; _previousTime = now; return rows.ToArray();
    }
}
