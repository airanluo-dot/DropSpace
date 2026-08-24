using System.Runtime.InteropServices;
using System.Text;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed record MonitorDescriptor(
    string Id,
    nint Handle,
    int Left,
    int Top,
    int Width,
    int Height,
    uint Dpi,
    bool IsPrimary,
    int? WorkLeft = null,
    int? WorkTop = null,
    int? WorkWidth = null,
    int? WorkHeight = null)
{
    public double Scale => Dpi / 96d;

    public int EffectiveWorkLeft => WorkLeft ?? Left;
    public int EffectiveWorkTop => WorkTop ?? Top;
    public int EffectiveWorkWidth => WorkWidth ?? Width;
    public int EffectiveWorkHeight => WorkHeight ?? Height;
}

public sealed class MonitorLayoutService(ILogger<MonitorLayoutService> logger)
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int DpiEffective = 0;
    private const int StyleIndex = -16;
    private const int ExtendedStyleIndex = -20;
    private const int DwmWindowAttributeCloaked = 14;

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
                (info.Flags & MonitorInfoPrimary) != 0,
                info.WorkArea.Left,
                info.WorkArea.Top,
                info.WorkArea.Right - info.WorkArea.Left,
                info.WorkArea.Bottom - info.WorkArea.Top));
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

    public MonitorDescriptor GetMonitorAtPoint(int x, int y)
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(monitor =>
                   x >= monitor.Left && x < monitor.Left + monitor.Width &&
                   y >= monitor.Top && y < monitor.Top + monitor.Height)
               ?? monitors.OrderBy(monitor => DistanceSquaredToBounds(monitor, x, y)).First();
    }

    private static long DistanceSquaredToBounds(MonitorDescriptor monitor, int x, int y)
    {
        var nearestX = Math.Clamp(x, monitor.Left, monitor.Left + monitor.Width - 1);
        var nearestY = Math.Clamp(y, monitor.Top, monitor.Top + monitor.Height - 1);
        var deltaX = (long)x - nearestX;
        var deltaY = (long)y - nearestY;
        return deltaX * deltaX + deltaY * deltaY;
    }

    public bool IsForegroundFullscreen(MonitorDescriptor monitor)
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero ||
            !GetWindowRect(foreground, out var bounds))
        {
            return false;
        }

        var foregroundMonitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var classNameBuffer = new StringBuilder(256);
        _ = GetClassName(foreground, classNameBuffer, classNameBuffer.Capacity);
        var className = classNameBuffer.ToString();
        var cloaked = 0;
        var cloakedResult = DwmGetWindowAttribute(
            foreground,
            DwmWindowAttributeCloaked,
            out cloaked,
            sizeof(int));
        _ = GetWindowThreadProcessId(foreground, out var processId);
        var facts = new ForegroundWindowFacts(
            IsWindowVisible(foreground),
            cloakedResult >= 0 && cloaked != 0,
            IsIconic(foreground),
            foreground == GetDesktopWindow() || foreground == GetShellWindow(),
            foregroundMonitor == monitor.Handle,
            bounds.Left <= monitor.Left &&
            bounds.Top <= monitor.Top &&
            bounds.Right >= monitor.Left + monitor.Width &&
            bounds.Bottom >= monitor.Top + monitor.Height,
            GetWindowLongPointer(foreground, StyleIndex).ToInt64(),
            GetWindowLongPointer(foreground, ExtendedStyleIndex).ToInt64(),
            className);
        var fullscreen = FullscreenWindowClassifier.IsFullscreenApplication(facts);
        if (fullscreen)
        {
            logger.LogDebug(
                "Foreground HWND {WindowHandle}, PID {ProcessId}, class {ClassName} is a visible uncloaked full-screen application on monitor {MonitorId}.",
                foreground,
                processId,
                className,
                monitor.Id);
        }

        return fullscreen;
    }

    private static nint GetWindowLongPointer(nint window, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, index)
        : new nint(GetWindowLong32(window, index));

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window,
        int attribute,
        out int value,
        int valueSize);
}
