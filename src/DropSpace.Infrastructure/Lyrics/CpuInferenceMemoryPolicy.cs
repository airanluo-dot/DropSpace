using System.Runtime.InteropServices;

namespace DropSpace.Infrastructure.Lyrics;

internal readonly record struct CpuMemorySnapshot(long AvailablePhysicalBytes, long AvailableCommitBytes);

/// <summary>Conservative startup admission; the job cap and watchdog still enforce runtime limits.</summary>
internal static class CpuInferenceMemoryPolicy
{
    internal const long SystemReserveBytes = 1024L * 1024 * 1024;

    internal static long RequiredAvailableBytes(long processMaximumBytes)
    {
        if (processMaximumBytes is < 268_435_456 or > WindowsInferenceProcess.Hy7BMaximumMemoryBytes)
            throw new ArgumentOutOfRangeException(nameof(processMaximumBytes));
        // This is an engineering allowance, not a measured peak or a promise that a
        // particular installed-RAM capacity can run either model.
        return processMaximumBytes + SystemReserveBytes;
    }

    internal static void EnsureAvailable(long processMaximumBytes, Func<CpuMemorySnapshot?> readSnapshot)
    {
        var required = RequiredAvailableBytes(processMaximumBytes);
        CpuMemorySnapshot? snapshot;
        try { snapshot = readSnapshot(); }
        catch (Exception error) when (error is ExternalException or IOException or UnauthorizedAccessException or
            InvalidOperationException or TypeLoadException)
        { throw new InferenceResourcesUnavailableException(); }
        if (snapshot is not { } memory || memory.AvailablePhysicalBytes < required || memory.AvailableCommitBytes < required)
            throw new InferenceResourcesUnavailableException();
    }

    internal static CpuMemorySnapshot? ReadWindowsSnapshot()
    {
        // Unsupported platforms have no production admission implementation. Process
        // fixtures inject bounded synthetic readings rather than skipping their tests.
        if (!OperatingSystem.IsWindows()) return null;
        // Read in the host, before the child enters its smaller Job. ullAvailPageFile
        // is the caller's available commit, which can be below system-wide availability.
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.AvailablePhysical > status.TotalPhysical ||
            status.AvailablePageFile > status.TotalPageFile || status.AvailablePhysical > long.MaxValue ||
            status.AvailablePageFile > long.MaxValue) return null;
        return new((long)status.AvailablePhysical, (long)status.AvailablePageFile);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
