using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DropSpace.App.Services;

/// <summary>
/// A never-shown, zero-sized message recipient for display broadcasts. Unlike the former top-edge
/// activation hosts it is not topmost, is not registered for OLE and never participates in hit testing.
/// </summary>
public sealed class DisplayTopologyWatcher : IDisposable
{
    private const uint WindowStylePopup = 0x80000000;
    private const uint ExtendedStyleToolWindow = 0x00000080;
    private const uint ExtendedStyleNoActivate = 0x08000000;
    private const uint WindowMessageDisplayChange = 0x007E;
    private const uint WindowMessageSettingChange = 0x001A;
    private const string WindowClassName = "DropSpace.DisplayTopologyWatcher.v1";
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, DisplayTopologyWatcher> Watchers = [];
    private static readonly WindowProcedureCallback WindowProcedure = StaticWindowProcedure;
    private static ushort _windowClass;
    private bool _disposed;

    public DisplayTopologyWatcher()
    {
        EnsureWindowClass();
        WindowHandle = CreateWindowEx(
            ExtendedStyleToolWindow | ExtendedStyleNoActivate,
            WindowClassName,
            string.Empty,
            WindowStylePopup,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            GetModuleHandle(null),
            nint.Zero);
        if (WindowHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The display-topology watcher could not be created.");
        }

        lock (Gate)
        {
            Watchers[WindowHandle] = this;
        }
    }

    public event EventHandler? Changed;

    public nint WindowHandle { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (Gate)
        {
            Watchers.Remove(WindowHandle);
        }

        _ = DestroyWindow(WindowHandle);
        _disposed = true;
    }

    private static void EnsureWindowClass()
    {
        lock (Gate)
        {
            if (_windowClass != 0)
            {
                return;
            }

            var windowClass = new WindowClassExtended
            {
                Size = (uint)Marshal.SizeOf<WindowClassExtended>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                Instance = GetModuleHandle(null),
                ClassName = WindowClassName,
            };
            _windowClass = RegisterClassEx(ref windowClass);
            if (_windowClass == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The display-topology watcher class could not be registered.");
            }
        }
    }

    private static nint StaticWindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message is WindowMessageDisplayChange or WindowMessageSettingChange)
        {
            DisplayTopologyWatcher? watcher;
            lock (Gate)
            {
                Watchers.TryGetValue(window, out watcher);
            }

            watcher?.Changed?.Invoke(watcher, EventArgs.Empty);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassExtended
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public nint SmallIcon;
    }

    private delegate nint WindowProcedureCallback(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassExtended windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
}
