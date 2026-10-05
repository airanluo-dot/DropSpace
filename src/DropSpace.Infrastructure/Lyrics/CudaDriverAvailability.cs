using System.Runtime.InteropServices;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Read-only NVIDIA driver probe. The driver stays provided by Windows;
/// CUDA libraries are supplied by the application, never by a PATH search.</summary>
public static class CudaDriverAvailability
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Init(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int IntResult(out int value);
    public static bool IsCompatible()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return false;
        IntPtr library = IntPtr.Zero;
        try
        {
            library = LoadLibraryExW("nvcuda.dll", IntPtr.Zero, 0x00000800); // System32 only
            if (library == IntPtr.Zero) return false;
            var init = Marshal.GetDelegateForFunctionPointer<Init>(NativeLibrary.GetExport(library, "cuInit"));
            var version = Marshal.GetDelegateForFunctionPointer<IntResult>(NativeLibrary.GetExport(library, "cuDriverGetVersion"));
            var count = Marshal.GetDelegateForFunctionPointer<IntResult>(NativeLibrary.GetExport(library, "cuDeviceGetCount"));
            return init(0) == 0 && version(out var v) == 0 && v >= 12090 &&
                count(out var devices) == 0 && devices > 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        { return false; }
        finally { if (library != IntPtr.Zero) NativeLibrary.Free(library); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);
}
