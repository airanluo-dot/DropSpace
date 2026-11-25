using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DropSpace.App.Services.Media;

internal static class AudioProcessTree
{
    // Process loopback includes descendants. Select a matching player process only
    // when every match belongs to its tree, even if the player hides all windows.
    internal static uint? CommonPlayerRoot(HashSet<uint> matches, Process[] processes)
    {
        using var snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (snapshot.IsInvalid || !Process32FirstW(snapshot, ref entry)) return null;
        var parents = new Dictionary<uint, uint>();
        do { parents[entry.Id] = entry.ParentId; } while (Process32NextW(snapshot, ref entry));

        var byId = processes.ToDictionary(p => (uint)p.Id);
        uint? commonRoot = null;
        foreach (var id in matches)
        {
            var root = id;
            var child = id;
            var seen = new HashSet<uint> { child };
            while (parents.TryGetValue(child, out var parent) && parent != 0 && seen.Add(parent) &&
                   byId.TryGetValue(parent, out var parentProcess) && byId.TryGetValue(child, out var childProcess))
            {
                // Windows may reuse the PID of an exited parent. A newer process
                // cannot be this child's parent and must never receive its capture.
                try { if (parentProcess.StartTime > childProcess.StartTime) break; }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { break; }
                if (matches.Contains(parent)) root = parent;
                child = parent;
            }
            if (commonRoot is not null && commonRoot != root) return null;
            commonRoot = root;
        }
        return commonRoot;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public nuint DefaultHeapId;
        public uint ModuleId, ThreadCount, ParentId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Executable;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
