using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DropSpace.Infrastructure.Dlc;

/// <summary>Redirected process created atomically inside its job, before any worker code runs.</summary>
internal sealed class ModuleWorkerProcess : IDisposable
{
    private const nuint HandleListAttribute = 0x20002, JobListAttribute = 0x2000D;
    private const uint CreateNoWindow = 0x08000000, ExtendedStartupInfo = 0x00080000, CreateSuspended = 0x4;
    private readonly Process _process;
    public StreamWriter StandardInput { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public bool HasExited => _process.HasExited;
    public event EventHandler? Exited { add => _process.Exited += value; remove => _process.Exited -= value; }
    public Task WaitForExitAsync(CancellationToken token = default) => _process.WaitForExitAsync(token);

    private ModuleWorkerProcess(Process process, StreamWriter input, StreamReader output, StreamReader error)
    { _process = process; StandardInput = input; StandardOutput = output; StandardError = error; }

    public static ModuleWorkerProcess Start(string executable, string directory, string session, int dataVersion, ModuleProcessJob job)
    {
        // Explicit handle inheritance keeps the host's other handles out of this worker.
        AnonymousPipeServerStream? input = null, output = null, error = null;
        nint attributes = 0, handles = 0, jobs = 0;
        var initialized = false;
        var jobAdded = false;
        Process? process = null;
        StreamWriter? writer = null;
        StreamReader? reader = null, errors = null;
        try
        {
            input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Require(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, input.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, output.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, error.ClientSafePipeHandle.DangerousGetHandle());
            Require(UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, handles, (nuint)(3 * IntPtr.Size), 0, 0));
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            job.Handle.DangerousAddRef(ref jobAdded);
            Marshal.WriteIntPtr(jobs, job.Handle.DangerousGetHandle());
            Require(UpdateProcThreadAttribute(attributes, 0, JobListAttribute, jobs, (nuint)IntPtr.Size, 0, 0));
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = input.ClientSafePipeHandle.DangerousGetHandle(),
                    Output = output.ClientSafePipeHandle.DangerousGetHandle(),
                    Error = error.ClientSafePipeHandle.DangerousGetHandle(),
                },
                Attributes = attributes,
            };
            // Only host-generated session and integer arguments are appended; the application path is explicit.
            var command = new StringBuilder("\"" + executable + "\" --module-session " + session +
                " --data-version " + dataVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Suspended until managed ownership/streams are ready; JOB_LIST already binds lifetime atomically.
            Require(CreateProcessW(executable, command, 0, 0, true, CreateNoWindow | ExtendedStartupInfo | CreateSuspended, 0, directory, ref startup, out var created));
            using var processHandle = new SafeFileHandle(created.Process, true);
            using var threadHandle = new SafeFileHandle(created.Thread, true);
            process = Process.GetProcessById(checked((int)created.ProcessId));
            _ = process.Handle; // acquire ownership while the native handle still prevents PID reuse
            process.EnableRaisingEvents = true;
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            writer = new StreamWriter(input, new UTF8Encoding(false));
            reader = new StreamReader(output, new UTF8Encoding(false));
            errors = new StreamReader(error, new UTF8Encoding(false));
            if (ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(process, writer, reader, errors);
        }
        catch
        {
            try { job.Terminate(); } catch (Win32Exception) { }
            process?.Dispose();
            try { writer?.Dispose(); } catch (IOException) { }
            reader?.Dispose(); errors?.Dispose();
            input?.Dispose(); output?.Dispose(); error?.Dispose();
            throw;
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (handles != 0) Marshal.FreeHGlobal(handles);
            if (jobs != 0) Marshal.FreeHGlobal(jobs);
            if (jobAdded) job.Handle.DangerousRelease();
        }
    }
    private static void Require(bool success)
    { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose()
    {
        try { StandardInput.Dispose(); } catch (IOException) { }
        finally
        {
            try { StandardOutput.Dispose(); }
            finally { try { StandardError.Dispose(); } finally { _process.Dispose(); } }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment,
        string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
}
