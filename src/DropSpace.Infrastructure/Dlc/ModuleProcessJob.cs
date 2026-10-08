using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DropSpace.Infrastructure.Dlc;

/// <summary>Windows lifetime/resource owner, not a permission sandbox. Closing the host handle kills its workers.</summary>
internal sealed class ModuleProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    public ModuleProcessJob()
    {
        _handle = CreateJobObjectW(0, null);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits { LimitFlags = 0x2000 },
        };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { var error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error); }
    }
    internal SafeFileHandle Handle => _handle;
    public void Terminate()
    {
        if (!TerminateJobObject(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public bool IsEmpty
    {
        get
        {
            if (!QueryInformationJobObject(_handle, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return accounting.ActiveProcesses == 0;
        }
    }
    public async Task<bool> WaitForEmptyAsync(TimeSpan timeout)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            if (IsEmpty) return true;
            await Task.Delay(20).ConfigureAwait(false);
        } while (deadline.Elapsed < timeout);
        return IsEmpty;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out Accounting information, uint length, nint returnedLength);
}
