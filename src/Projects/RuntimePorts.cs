using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevWorkspace;

internal static class RuntimePorts
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    internal static async Task RefreshAsync(WorkspaceOperation[] operations, CancellationToken token)
    {
        var parents = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
            if (Process32FirstW(snapshot, ref entry))
                do { parents[(int)entry.Id] = (int)entry.Parent; } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        var (code, output, error) = await ProjectTools.CaptureAsync(ProjectTools.CreateCaptureInfo(System.IO.Path.Combine(Environment.SystemDirectory, "netstat.exe"), Environment.SystemDirectory, ["-ano", "-p", "tcp"]), "netstat", 10, token);
        if (code != 0) throw new System.IO.IOException(error);
        var ports = Parse(output);
        foreach (var operation in operations)
        {
            operation.Ports = "—";
            if (!operation.IsRunning || operation.ProcessId is not int root) continue;
            try
            {
                using var process = Process.GetProcessById(root);
                if (operation.ProcessStartedUtc != process.StartTime.ToUniversalTime()) continue;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            var owned = new HashSet<int> { root };
            bool changed;
            do
            {
                changed = false;
                foreach (var (id, parent) in parents) if (owned.Contains(parent)) changed |= owned.Add(id);
            } while (changed);
            operation.Ports = string.Join(", ", ports.Where(p => owned.Contains(p.Pid)).Select(p => p.Address).Distinct());
            if (operation.Ports.Length == 0) operation.Ports = "—";
        }
    }

    internal static (int Pid, string Address)[] Parse(string output)
    {
        var ports = new List<(int, string)>();
        foreach (string line in output.Split('\n'))
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 5 && parts[0] == "TCP" && parts[3] == "LISTENING" && int.TryParse(parts[4], out int pid)) ports.Add((pid, parts[1]));
        }
        return ports.ToArray();
    }
}
