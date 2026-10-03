using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>
/// Native resource/lifetime limits, not a security sandbox or network boundary. The job is attached
/// atomically during CreateProcess, before any child code runs. No permissive fallback is allowed.
/// </summary>
internal static class WindowsInferenceProcess
{
    internal const long MaximumMemoryBytes = 3L * 1024 * 1024 * 1024;
    internal const long Hy7BMaximumMemoryBytes = 12L * 1024 * 1024 * 1024;
    private const uint LimitFlags = 0x00000008 | 0x00000100 | 0x00000200 | 0x00002000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;

    [SupportedOSPlatform("windows")]
    internal static LocalInferenceProcess Start(ProcessStartInfo start, long memoryLimitBytes = MaximumMemoryBytes,
        bool retainStandardInput = false)
    {
        if (memoryLimitBytes is < 268_435_456 or > Hy7BMaximumMemoryBytes) throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes));
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Local inference requires 64-bit Windows.");
        SafeJobHandle? job = null;
        Process? process = null;
        StreamReader? output = null;
        StreamReader? errors = null;
        StreamWriter? input = null;
        try
        {
            job = CreateLimitedJob(memoryLimitBytes);
            using var stdout = CreateRedirectedPipe(parentReads: true);
            using var stderr = CreateRedirectedPipe(parentReads: true);
            using var stdin = CreateRedirectedPipe(parentReads: false);
            using var attributes = new ProcessAttributes(job, stdin.Child, stdout.Child, stderr.Child);
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x00000100,
                    StandardInput = stdin.Child.DangerousGetHandle(),
                    StandardOutput = stdout.Child.DangerousGetHandle(),
                    StandardError = stderr.Child.DangerousGetHandle(),
                },
                AttributeList = attributes.Pointer,
            };
            var environment = Marshal.StringToHGlobalUni(BuildEnvironment(start));
            try
            {
                var command = new StringBuilder(BuildCommandLine(start));
                if (!CreateProcess(Path.GetFullPath(start.FileName), command, IntPtr.Zero, IntPtr.Zero, true,
                    CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow,
                    environment, Path.GetDirectoryName(Path.GetFullPath(start.FileName)), ref startup, out var info))
                    throw NativeFailure("Unable to launch local inference with mandatory Windows limits.");
                using var nativeProcess = new SafeKernelHandle(info.Process);
                using var thread = new SafeKernelHandle(info.Thread);
                try
                {
                    // The thread is suspended and already inside the job, so even parent failure here
                    // closes the non-inherited job handle and terminates the child.
                    process = Process.GetProcessById(checked((int)info.ProcessId));
                    _ = process.Handle;
                    output = new StreamReader(stdout.TakeParentStream(), Encoding.UTF8);
                    errors = new StreamReader(stderr.TakeParentStream(), Encoding.UTF8);
                    if (retainStandardInput)
                        input = new StreamWriter(stdin.TakeParentStream(), new UTF8Encoding(false)) { AutoFlush = true };
                    else stdin.Parent.Dispose(); // Immediate EOF; the runtime cannot request interactive input.
                    if (ResumeThread(thread) == uint.MaxValue)
                        throw NativeFailure("Unable to resume bounded local inference.");
                    return new LocalInferenceProcess(process, output, errors, input, job);
                }
                catch
                {
                    _ = TerminateProcess(nativeProcess, 1);
                    throw;
                }
            }
            finally { Marshal.FreeHGlobal(environment); }
        }
        catch
        {
            job?.Dispose();
            output?.Dispose();
            errors?.Dispose();
            input?.Dispose();
            process?.Dispose();
            throw;
        }
    }

    internal static string BuildCommandLine(ProcessStartInfo start) =>
        string.Join(' ', new[] { Path.GetFullPath(start.FileName) }.Concat(start.ArgumentList).Select(QuoteArgument));

    [SupportedOSPlatform("windows")]
    internal static async Task WaitForExitSignalAsync(SafeProcessHandle process)
    {
        // Process.HasExited (also used by .NET 10 WaitForExitAsync) can observe an
        // exit code before the kernel process object signals. Only that signal
        // confirms termination and image teardown; package leases must survive it.
        // Duplicate the already-open handle, never reopen a possibly recycled PID.
        using var wait = new ProcessExitWaitHandle(process);
        if (wait.WaitOne(0)) return;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(wait,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), exited,
            Timeout.Infinite, executeOnlyOnce: true);
        try { await exited.Task.ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }

    internal static string QuoteArgument(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("A process argument contains a null character.");
        var text = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') text.Append('\\', slashes * 2 + 1).Append('"');
            else text.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static string BuildEnvironment(ProcessStartInfo start) =>
        string.Join('\0', start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";

    private static SafeJobHandle CreateLimitedJob(long memoryLimitBytes)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw NativeFailure("Unable to create inference limits."); }
        try
        {
            var limits = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = LimitFlags, ActiveProcessLimit = 1 },
                ProcessMemoryLimit = (nuint)memoryLimitBytes,
                JobMemoryLimit = (nuint)memoryLimitBytes,
            };
            var length = (uint)Marshal.SizeOf<ExtendedLimitInformation>();
            if (!SetInformationJobObject(job, 9, ref limits, length) ||
                !QueryInformationJobObject(job, 9, out var actual, length, IntPtr.Zero) ||
                actual.BasicLimitInformation.LimitFlags != LimitFlags || actual.BasicLimitInformation.ActiveProcessLimit != 1 ||
                actual.ProcessMemoryLimit != (nuint)memoryLimitBytes || actual.JobMemoryLimit != (nuint)memoryLimitBytes)
                throw NativeFailure("Unable to enforce mandatory inference resource limits.");
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    private static RedirectedPipe CreateRedirectedPipe(bool parentReads)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out var read, out var write, ref security, 0))
            throw NativeFailure("Unable to create local inference streams.");
        var parent = parentReads ? read : write;
        var child = parentReads ? write : read;
        if (!SetHandleInformation(parent, 1, 0))
        {
            read.Dispose(); write.Dispose();
            throw NativeFailure("Unable to restrict inference handle inheritance.");
        }
        return new RedirectedPipe(parent, child, parentReads);
    }

    private static IOException NativeFailure(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private sealed class RedirectedPipe(SafeFileHandle parent, SafeFileHandle child, bool parentReads) : IDisposable
    {
        private bool _transferred;
        internal SafeFileHandle Parent { get; } = parent;
        internal SafeFileHandle Child { get; } = child;
        internal FileStream TakeParentStream()
        {
            var stream = new FileStream(Parent, parentReads ? FileAccess.Read : FileAccess.Write, 4096, isAsync: false);
            _transferred = true;
            return stream;
        }
        public void Dispose() { Child.Dispose(); if (!_transferred) Parent.Dispose(); }
    }

    private sealed class ProcessAttributes : IDisposable
    {
        private IntPtr _handles;
        private IntPtr _job;
        private bool _initialized;
        internal IntPtr Pointer { get; private set; }
        internal ProcessAttributes(SafeJobHandle job, params SafeFileHandle[] handles)
        {
            try
            {
                nuint size = 0;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                if (size == 0) throw NativeFailure("Windows process attributes are unavailable.");
                Pointer = Marshal.AllocHGlobal(checked((nint)size));
                if (!InitializeProcThreadAttributeList(Pointer, 2, 0, ref size))
                    throw NativeFailure("Unable to initialize inference process attributes.");
                _initialized = true;
                _job = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_job, job.DangerousGetHandle());
                _handles = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                for (var index = 0; index < handles.Length; index++)
                    Marshal.WriteIntPtr(_handles, index * IntPtr.Size, handles[index].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(Pointer, 0, 0x0002000D, _job, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero) ||
                    !UpdateProcThreadAttribute(Pointer, 0, 0x00020002, _handles, (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                    throw NativeFailure("Windows cannot enforce the inference job and handle restrictions.");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(_handles); Marshal.FreeHGlobal(_job);
            Pointer = IntPtr.Zero; _handles = IntPtr.Zero; _job = IntPtr.Zero; _initialized = false;
        }
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        internal ProcessExitWaitHandle(SafeProcessHandle process)
        {
            var currentProcess = GetCurrentProcess();
            if (!DuplicateHandle(currentProcess, process, currentProcess, out var duplicate,
                    0, false, 0x00000002 /* DUPLICATE_SAME_ACCESS */))
            {
                var error = NativeFailure("Unable to retain the inference process exit signal.");
                duplicate.Dispose();
                throw error;
            }
            SafeWaitHandle = duplicate;
        }
    }
    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeKernelHandle(IntPtr value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size; internal IntPtr Reserved; internal IntPtr Desktop; internal IntPtr Title;
        internal uint X; internal uint Y; internal uint XSize; internal uint YSize; internal uint XCountChars;
        internal uint YCountChars; internal uint FillAttribute; internal uint Flags; internal ushort ShowWindow;
        internal ushort ReservedBytes; internal IntPtr ReservedPointer; internal IntPtr StandardInput;
        internal IntPtr StandardOutput; internal IntPtr StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { internal IntPtr Process; internal IntPtr Thread; internal uint ProcessId; internal uint ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        internal long PerProcessUserTimeLimit; internal long PerJobUserTimeLimit; internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize; internal nuint MaximumWorkingSetSize; internal uint ActiveProcessLimit;
        internal nuint Affinity; internal uint PriorityClass; internal uint SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount; internal ulong WriteOperationCount; internal ulong OtherOperationCount;
        internal ulong ReadTransferCount; internal ulong WriteTransferCount; internal ulong OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        internal BasicLimitInformation BasicLimitInformation; internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit; internal nuint JobMemoryLimit; internal nuint PeakProcessMemoryUsed; internal nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref ExtendedLimitInformation information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass, out ExtendedLimitInformation information, uint size, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeKernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle source, IntPtr targetProcess,
        out SafeWaitHandle target, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
