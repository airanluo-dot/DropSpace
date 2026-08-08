using System.Runtime.InteropServices;

namespace DropSpace.App.Services;

public sealed record MonitorDescriptor(
    string Id,
    nint Handle,
    int Left,
    int Top,
    int Width,
    int Height,
    uint Dpi,
    bool IsPrimary)
{
    public double Scale => Dpi / 96d;
}

public sealed class MonitorLayoutService
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int DpiEffective = 0;

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        EnumDisplayMonitors(nint.Zero, nint.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<MonitorInfo>(),
            };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var dpi = GetMonitorDpi(monitor);
            monitors.Add(new MonitorDescriptor(
                monitor.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture),
                monitor,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top,
                dpi,
                (info.Flags & MonitorInfoPrimary) != 0));
            return true;
        }, nint.Zero);

        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows did not report an active display.");
        }

        return monitors;
    }

    public MonitorDescriptor GetPrimaryMonitor()
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
    }

    public bool IsForegroundFullscreen(MonitorDescriptor monitor)
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero || !GetWindowRect(foreground, out var bounds))
        {
            return false;
        }

        var foregroundMonitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        return foregroundMonitor == monitor.Handle &&
               bounds.Left <= monitor.Left &&
               bounds.Top <= monitor.Top &&
               bounds.Right >= monitor.Left + monitor.Width &&
               bounds.Bottom >= monitor.Top + monitor.Height;
    }

    private static uint GetMonitorDpi(nint monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, DpiEffective, out var dpiX, out _) >= 0 && dpiX > 0
                ? dpiX
                : 96;
        }
        catch (DllNotFoundException)
        {
            return 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private delegate bool MonitorEnumerationCallback(
        nint monitor,
        nint deviceContext,
        nint monitorRectangle,
        nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumerationCallback callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
}
