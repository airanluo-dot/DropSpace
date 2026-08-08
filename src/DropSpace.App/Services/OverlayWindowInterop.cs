using System.Runtime.InteropServices;
using DropSpace.Core.Models;

namespace DropSpace.App.Services;

internal static class OverlayWindowInterop
{
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedStyleAppWindow = 0x00040000L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;
    private const int ShowNoActivate = 4;
    private const int RegionOr = 2;
    private static readonly nint Topmost = new(-1);

    public static void ConfigureToolWindow(nint window)
    {
        var style = GetWindowLongPointer(window, ExtendedStyleIndex).ToInt64();
        style |= ExtendedStyleToolWindow | ExtendedStyleNoActivate;
        style &= ~ExtendedStyleAppWindow;
        SetWindowLongPointer(window, ExtendedStyleIndex, new nint(style));
        SetWindowPos(
            window,
            Topmost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove |
            SetWindowPositionNoSize |
            SetWindowPositionNoActivate |
            SetWindowPositionFrameChanged);
        ShowWindow(window, ShowNoActivate);
    }

    public static void SetNoActivate(nint window, bool noActivate)
    {
        var style = GetWindowLongPointer(window, ExtendedStyleIndex).ToInt64();
        style = noActivate
            ? style | ExtendedStyleNoActivate
            : style & ~ExtendedStyleNoActivate;
        SetWindowLongPointer(window, ExtendedStyleIndex, new nint(style));
        SetWindowPos(
            window,
            Topmost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove |
            SetWindowPositionNoSize |
            SetWindowPositionNoActivate |
            SetWindowPositionFrameChanged);
    }

    public static void ApplyRegion(
        nint window,
        int width,
        int height,
        int topOffset,
        int radius,
        OverlayDisplayMode displayMode)
    {
        var region = displayMode == OverlayDisplayMode.Notch
            ? CreateNotchRegion(width, height, radius)
            : CreateRoundRectRgn(0, topOffset, width + 1, height + 1, radius * 2, radius * 2);
        if (region == nint.Zero)
        {
            return;
        }

        if (SetWindowRgn(window, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    public static void ApplyActivationRegion(nint window, int width, int height)
    {
        var region = CreateRectRgn(0, 0, width, height);
        if (region != nint.Zero && SetWindowRgn(window, region, false) == 0)
        {
            DeleteObject(region);
        }
    }

    private static nint CreateNotchRegion(int width, int height, int radius)
    {
        var top = CreateRectRgn(0, 0, width + 1, Math.Max(1, height - radius));
        var bottom = CreateRoundRectRgn(
            0,
            Math.Max(0, height - radius * 2),
            width + 1,
            height + 1,
            radius * 2,
            radius * 2);
        if (top == nint.Zero || bottom == nint.Zero)
        {
            if (top != nint.Zero)
            {
                DeleteObject(top);
            }

            if (bottom != nint.Zero)
            {
                DeleteObject(bottom);
            }

            return nint.Zero;
        }

        CombineRgn(top, top, bottom, RegionOr);
        DeleteObject(bottom);
        return top;
    }

    private static nint GetWindowLongPointer(nint window, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, index)
        : new nint(GetWindowLong32(window, index));

    private static nint SetWindowLongPointer(nint window, int index, nint value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(window, index, value)
        : new nint(SetWindowLong32(window, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
