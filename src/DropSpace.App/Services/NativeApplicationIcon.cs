using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace DropSpace.App.Services;

/// <summary>
/// Loads the canonical icon from the executable's RT_GROUP_ICON resource. A bundled single-file
/// build has no guaranteed Assets/AppIcon.ico beside the process, so path-only icon loading can
/// silently fall back to the generic Win32 executable icon on another machine.
/// </summary>
internal static class NativeApplicationIcon
{
    private const int ApplicationIconResourceId = 101;
    private const uint ImageIcon = 1;
    private const uint LoadResourceShared = 0x00008000;
    private const uint WindowMessageSetIcon = 0x0080;
    private static readonly nint IconSmall = nint.Zero;
    private static readonly nint IconBig = new(1);
    private const int SystemMetricIconWidth = 11;
    private const int SystemMetricIconHeight = 12;
    private const int SystemMetricSmallIconWidth = 49;
    private const int SystemMetricSmallIconHeight = 50;

    public static void ApplyToWindow(nint window, AppWindow appWindow)
    {
        if (window == nint.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(window));
        }
        var small = LoadSharedIcon(SystemMetricSmallIconWidth, SystemMetricSmallIconHeight);
        var big = LoadSharedIcon(SystemMetricIconWidth, SystemMetricIconHeight);
        SendMessage(window, WindowMessageSetIcon, IconSmall, small);
        SendMessage(window, WindowMessageSetIcon, IconBig, big);

        // Keep AppWindow's icon source aligned when a physical content file is available (for
        // developer/MSIX layouts). The embedded resource above is the portable-build authority.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }
    }

    public static nint LoadSharedTrayIcon() =>
        LoadSharedIcon(SystemMetricSmallIconWidth, SystemMetricSmallIconHeight);

    private static nint LoadSharedIcon(int widthMetric, int heightMetric)
    {
        var module = GetModuleHandle(null);
        var icon = LoadImage(
            module,
            new nint(ApplicationIconResourceId),
            ImageIcon,
            GetSystemMetrics(widthMetric),
            GetSystemMetrics(heightMetric),
            LoadResourceShared);
        return icon != nint.Zero
            ? icon
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "The embedded DropSpace icon could not be loaded.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    private static extern nint LoadImage(
        nint instance,
        nint name,
        uint type,
        int width,
        int height,
        uint load);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
