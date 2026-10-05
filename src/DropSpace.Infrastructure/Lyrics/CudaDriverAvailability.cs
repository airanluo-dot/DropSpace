using System.Runtime.InteropServices;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Read-only NVIDIA driver probe. The driver stays provided by Windows;
/// CUDA libraries are supplied by the application, never by a PATH search.</summary>
public static class CudaDriverAvailability
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Init(uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int IntResult(out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Attribute(out int value, int attribute, int device);
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
            if (init(0) != 0 || version(out var v) != 0 || v < 13040 ||
                count(out var devices) != 0 || devices <= 0 || devices > 64) return false;
            var attribute = Marshal.GetDelegateForFunctionPointer<Attribute>(NativeLibrary.GetExport(library, "cuDeviceGetAttribute"));
            for (var device = 0; device < devices; device++)
                // CUDA13 dropped offline code generation below compute capability7.5.
                if (attribute(out var major, 75, device) == 0 && attribute(out var minor, 76, device) == 0 &&
                    (major > 7 || major == 7 && minor >= 5)) return true;
            return false;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        { return false; }
        finally { if (library != IntPtr.Zero) NativeLibrary.Free(library); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);
}
