using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DevWorkspace;

// Every running copy holds a shared file handle outside the replaceable install
// directory. An update requires exclusive access; it cannot evict another copy.
internal sealed class UpdateInstanceLease : IDisposable
{
    private readonly string _path;
    private readonly Mutex _gate;
    private FileStream? _handle;
    internal bool IsExclusive { get; private set; }

    internal UpdateInstanceLease(string installPath, string stateDirectory)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(installPath).ToUpperInvariant())));
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, key + ".lock");
        _gate = new Mutex(false, @"Local\DevWorkspace.Update." + key);
        try
        {
            // A concurrently applying update briefly owns the exclusive handle.
            // Wait without holding the gate, so it can finish or release its lease.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    WithGate(() =>
                    {
                        if (!File.Exists(_path))
                            using (File.Open(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                        _handle = Shared();
                    });
                    break;
                }
                catch (IOException) when (attempt < 100) { Thread.Sleep(100); }
            }
        }
        catch { _gate.Dispose(); throw; }
    }

    internal bool TryAcquireExclusive()
    {
        bool acquired = false;
        WithGate(() =>
        {
            _handle?.Dispose();
            try
            {
                _handle = File.Open(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                IsExclusive = acquired = true;
            }
            catch (IOException) { _handle = Shared(); }
            catch { _handle = Shared(); throw; }
        });
        return acquired;
    }

    internal void ReleaseExclusive()
    {
        if (!IsExclusive) return;
        WithGate(() =>
        {
            _handle?.Dispose();
            _handle = Shared();
            IsExclusive = false;
        });
    }

    private FileStream Shared() => File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private void WithGate(Action action)
    {
        bool entered;
        try { entered = _gate.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { entered = true; }
        if (!entered) throw new IOException("Update instance lock is busy.");
        try { action(); }
        finally { _gate.ReleaseMutex(); }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
        _gate.Dispose();
    }
}
