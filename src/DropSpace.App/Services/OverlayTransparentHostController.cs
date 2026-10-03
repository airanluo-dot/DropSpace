using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DropSpace.App.Services;

/// <summary>
/// Keeps the overlay's own top-level client from receiving WinUI's opaque erase fill.
/// Install on its UI thread before XAML initialization; the transparent Window backdrop
/// still supplies the composition target. All other messages retain normal processing.
/// </summary>
internal sealed class OverlayTransparentHostController : IDisposable
{
    private const uint EraseBackgroundMessage = 0x0014;
    private const uint NonClientDestroyMessage = 0x0082;
    private const nuint SubclassId = 0x44534552;
    private readonly SubclassProc _callback;
    private GCHandle _callbackLifetime;
    private nint _window;

    internal bool IsAttached => _window != nint.Zero;
    internal long HandledEraseCount { get; private set; }

    internal OverlayTransparentHostController(nint window)
    {
        if (window == nint.Zero)
            throw new ArgumentException("A valid overlay HWND is required.", nameof(window));

        _window = window;
        _callback = WindowSubclassProc;
        // A native callback does not keep a managed delegate alive. Retain this owner
        // until removal succeeds or the HWND is destroyed, including cleanup failures.
        _callbackLifetime = GCHandle.Alloc(this);
        if (!SetWindowSubclass(window, _callback, SubclassId, 0))
        {
            var error = Marshal.GetLastWin32Error();
            ReleaseLifetime();
            throw new Win32Exception(error, "The transparent overlay erase hook could not be installed.");
        }
    }

    public void Dispose()
    {
        if (_window == nint.Zero) return;
        if (!RemoveWindowSubclass(_window, _callback, SubclassId) && IsWindow(_window))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The transparent overlay erase hook could not be removed.");

        ReleaseLifetime();
    }

    private nint WindowSubclassProc(nint window, uint message, nuint wParam,
        nint lParam, nuint subclassId, nuint referenceData)
    {
        if (window == _window && message == EraseBackgroundMessage)
        {
            HandledEraseCount++;
            return 1;
        }
        if (window == _window && message == NonClientDestroyMessage)
        {
            if (RemoveWindowSubclass(window, _callback, SubclassId))
                _window = nint.Zero;
            try { return DefSubclassProc(window, message, wParam, lParam); }
            finally { ReleaseLifetime(); }
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ReleaseLifetime()
    {
        _window = nint.Zero;
        if (_callbackLifetime.IsAllocated) _callbackLifetime.Free();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nuint wParam,
        nint lParam, nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
}
