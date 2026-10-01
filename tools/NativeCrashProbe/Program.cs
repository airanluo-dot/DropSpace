using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

if (!OperatingSystem.IsWindows() || args.Length != 2) throw new ArgumentException("Windows: NativeCrashProbe EXE OUTPUT_JSON");
if (Environment.GetEnvironmentVariable("DROPSPACE_TEST_DATA_ROOT") is not { Length: > 0 }) throw new InvalidOperationException("An isolated test-data root is required.");
var records = new List<object>();
var start = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
var command = new StringBuilder($"\"{Path.GetFullPath(args[0])}\" --test-mode --smoke-test --smoke-language en-US");
if (!Native.CreateProcess(null, command, 0, 0, false, 2, 0, null, ref start, out var process)) throw new Win32Exception(Marshal.GetLastWin32Error());
var deadline = Stopwatch.StartNew();
var exited = false;
try
{
    while (deadline.Elapsed < TimeSpan.FromSeconds(180))
    {
        var evt = new Native.DebugEvent();
        if (!Native.WaitForDebugEvent(ref evt, 1000)) continue;
        uint continuation = 0x00010002;
        if (evt.Code is 3 or 6 && evt.UnionHandle != 0) Native.CloseHandle(evt.UnionHandle);
        if (evt.Code == 1)
        {
            continuation = evt.ExceptionCode == 0x80000003 ? 0x00010002u : 0x80010001u;
            if (evt.ExceptionCode == 0xc0000409 || (evt.ExceptionCode == 0xc0000005 && evt.FirstChance == 0))
            {
                using var managed = Process.GetProcessById((int)process.ProcessId);
                var modules = managed.Modules.Cast<ProcessModule>().Select(m => new Module(m.ModuleName, (ulong)m.BaseAddress.ToInt64(), (ulong)m.ModuleMemorySize)).ToArray();
                string? Describe(ulong address)
                {
                    var module = modules.FirstOrDefault(m => address >= m.Base && address - m.Base < m.Size);
                    return module is null ? null : $"{module.Name}+0x{address - module.Base:x}";
                }
                var thread = Native.OpenThread(0x48, false, evt.ThreadId);
                var raw = Marshal.AllocHGlobal(1250);
                try
                {
                    var context = (nint)((raw.ToInt64() + 15) & ~15L);
                    Marshal.Copy(new byte[1232], 0, context, 1232);
                    Marshal.WriteInt32(context, 48, 0x100003);
                    var valid = thread != 0 && Native.GetThreadContext(thread, context);
                    var stackCandidates = new List<string>();
                    string? instruction = null;
                    if (valid)
                    {
                        instruction = Describe((ulong)Marshal.ReadInt64(context, 248));
                        var stack = new byte[8192];
                        var rsp = (nint)Marshal.ReadInt64(context, 152);
                        if (Native.ReadProcessMemory(process.Process, rsp, stack, (nuint)stack.Length, out var read))
                        {
                            for (var offset = 0; offset + 8 <= (int)read; offset += 8)
                            {
                                var symbol = Describe(BitConverter.ToUInt64(stack, offset));
                                if (symbol is not null) stackCandidates.Add($"stack+0x{offset:x}: {symbol}");
                            }
                        }
                    }
                    records.Add(new { exceptionCode = $"0x{evt.ExceptionCode:x8}", firstChance = evt.FirstChance, threadId = evt.ThreadId, mainThreadId = process.ThreadId,
                        exceptionAddress = Describe((ulong)evt.ExceptionAddress.ToInt64()), instruction, contextRead = valid,
                        parameterCount = evt.ParameterCount, failFastSubcode = evt.ParameterCount > 0 ? evt.Parameter0 : 0,
                        stackAddressCandidates = stackCandidates.Take(160).ToArray() });
                    // Only module-relative addresses are retained. No raw dump, registers,
                    // environment, arbitrary stack bytes or process strings are exported.
                    File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { Marshal.FreeHGlobal(raw); if (thread != 0) Native.CloseHandle(thread); }
            }
        }
        if (evt.Code == 5)
        {
            records.Add(new { exitCode = evt.ExitCode });
            Native.ContinueDebugEvent(evt.ProcessId, evt.ThreadId, continuation);
            exited = true;
            break;
        }
        Native.ContinueDebugEvent(evt.ProcessId, evt.ThreadId, continuation);
    }
}
finally
{
    if (!exited) { Native.TerminateProcess(process.Process, 1460); records.Add(new { timedOut = true }); }
    File.WriteAllText(args[1], JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    Native.CloseHandle(process.Thread); Native.CloseHandle(process.Process);
}

internal sealed record Module(string Name, ulong Base, ulong Size);
internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] internal struct StartupInfo
    {
        public int cb; public string? reserved, desktop, title;
        public int x,y,xSize,ySize,xCountChars,yCountChars,fillAttribute,flags;
        public short showWindow,reserved2; public nint reservedPtr,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public nint Process,Thread; public uint ProcessId,ThreadId; }
    [StructLayout(LayoutKind.Explicit, Size=176)] internal struct DebugEvent
    {
        [FieldOffset(0)] public uint Code;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(16)] public nint UnionHandle;
        [FieldOffset(16)] public uint ExceptionCode;
        [FieldOffset(16)] public uint ExitCode;
        [FieldOffset(32)] public nint ExceptionAddress;
        [FieldOffset(40)] public uint ParameterCount;
        [FieldOffset(48)] public ulong Parameter0;
        [FieldOffset(168)] public uint FirstChance;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern bool CreateProcess(string? application,StringBuilder command,nint processAttributes,nint threadAttributes,bool inherit,uint flags,nint environment,string? directory,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool WaitForDebugEvent(ref DebugEvent evt,uint milliseconds);
    [DllImport("kernel32.dll")] internal static extern bool ContinueDebugEvent(uint process,uint thread,uint status);
    [DllImport("kernel32.dll")] internal static extern nint OpenThread(uint access,bool inherit,uint id);
    [DllImport("kernel32.dll")] internal static extern bool GetThreadContext(nint thread,nint context);
    [DllImport("kernel32.dll")] internal static extern bool ReadProcessMemory(nint process,nint address,byte[] data,nuint count,out nuint read);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(nint process,uint code);
}
