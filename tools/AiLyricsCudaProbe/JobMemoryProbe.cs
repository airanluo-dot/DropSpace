using System.Reflection;
using System.Runtime.InteropServices;
using DropSpace.Infrastructure.Lyrics;

// Observe the existing production Job without changing any limit. Windows messages 9/10
// identify failed process/job commit allocations; dedicated VRAM is measured separately.
internal sealed class JobMemoryProbe : IDisposable
{
    private readonly Dictionary<int, nint> _ports = [];
    public List<object> Events { get; } = [];
    internal object? Sample(PersistentPlainLyricsRunner runner, int pid)
    {
        var session = typeof(PersistentPlainLyricsRunner).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runner);
        var child = session?.GetType().GetProperty("Child", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session);
        var limits = child?.GetType().GetField("_limits", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(child) as SafeHandle;
        if (limits is null || limits.IsClosed) return null;
        try
        {
            if (!_ports.ContainsKey(pid))
            {
                var port = CreateIoCompletionPort(new nint(-1), 0, 0, 1);
                if (port == 0) return null;
                var association = new Association { Key = (nuint)pid, Port = port };
                if (!SetInformationJobObject(limits, 7, ref association, 16))
                { CloseHandle(port); return null; }
                _ports.Add(pid, port);
            }
            Drain();
            var buffer = Marshal.AllocHGlobal(144);
            try
            {
                if (!QueryInformationJobObject(limits, 9, buffer, 144, 0)) return null;
                return new { processLimit = Marshal.ReadInt64(buffer + 112), jobLimit = Marshal.ReadInt64(buffer + 120),
                    peakProcessCommit = Marshal.ReadInt64(buffer + 128), peakJobCommit = Marshal.ReadInt64(buffer + 136) };
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (ObjectDisposedException) { return null; }
    }
    internal void Drain()
    {
        foreach (var (pid, port) in _ports)
            while (GetQueuedCompletionStatus(port, out var message, out _, out _, 0))
                Events.Add(new { pid, message, memoryLimitReached = message is 9 or 10 });
    }
    public void Dispose() { foreach (var port in _ports.Values) CloseHandle(port); }
    [StructLayout(LayoutKind.Sequential)] private struct Association { internal nuint Key; internal nint Port; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateIoCompletionPort(nint file, nint port, nuint key, uint concurrency);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeHandle job, int kind, ref Association information, uint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeHandle job, int kind, nint information, uint bytes, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetQueuedCompletionStatus(nint port, out uint bytes, out nuint key, out nint overlapped, uint wait);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
