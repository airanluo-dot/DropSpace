using System.Runtime.InteropServices;
using DropSpace.Core.DragDrop;

namespace DropSpace.App.Services;

internal static class OverlayWindowInterop
{
    private const int ExtendedStyleIndex = -20;
    private const int StyleIndex = -16;
    private const long ExtendedStyleAppWindow = 0x00040000L;
    private const long ExtendedStyleClientEdge = 0x00000200L;
    private const long ExtendedStyleWindowEdge = 0x00000100L;
    private const long ExtendedStyleDialogModalFrame = 0x00000001L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long StyleBorder = 0x00800000L;
    private const long StyleCaption = 0x00C00000L;
    private const long StyleDialogFrame = 0x00400000L;
    private const long StyleThickFrame = 0x00040000L;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;
    private const int ShowNoActivate = 4;
    private const int ShowHide = 0;
    private const int RegionOr = 2;
    private const int DwmWindowAttributeNonClientRenderingPolicy = 2;
    private const int DwmWindowAttributeCornerPreference = 33;
    private const int DwmWindowAttributeBorderColor = 34;
    private const int DwmNonClientRenderingDisabled = 1;
    private const int DwmCornerDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private static readonly nint Topmost = new(-1);

    public static void ConfigureVisualWindow(nint window)
    {
        var extendedStyle = GetWindowLongPointer(window, ExtendedStyleIndex).ToInt64();
        extendedStyle |= ExtendedStyleToolWindow | ExtendedStyleNoActivate;
        extendedStyle &= ~(ExtendedStyleAppWindow |
                           ExtendedStyleClientEdge |
                           ExtendedStyleWindowEdge |
                           ExtendedStyleDialogModalFrame);
        SetWindowLongPointer(window, ExtendedStyleIndex, new nint(extendedStyle));

        var style = GetWindowLongPointer(window, StyleIndex).ToInt64();
        style &= ~(StyleBorder | StyleCaption | StyleDialogFrame | StyleThickFrame);
        SetWindowLongPointer(window, StyleIndex, new nint(style));

        var nonClientPolicy = DwmNonClientRenderingDisabled;
        DwmSetWindowAttribute(
            window,
            DwmWindowAttributeNonClientRenderingPolicy,
            ref nonClientPolicy,
            sizeof(int));
        var cornerPreference = DwmCornerDoNotRound;
        DwmSetWindowAttribute(
            window,
            DwmWindowAttributeCornerPreference,
            ref cornerPreference,
            sizeof(int));
        var borderColor = DwmColorNone;
        DwmSetWindowAttribute(
            window,
            DwmWindowAttributeBorderColor,
            ref borderColor,
            sizeof(uint));
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

    public static void ShowNoActivateAndTopmost(nint window)
    {
        SetWindowPos(
            window,
            Topmost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove |
            SetWindowPositionNoSize |
            SetWindowPositionNoActivate);
        ShowWindow(window, ShowNoActivate);
    }

    public static void Hide(nint window) => ShowWindow(window, ShowHide);

    public static bool TryGetCursorPosition(out DragScreenPoint point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = new DragScreenPoint(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    public static VisibleWindowProbe ProbeWindowAtPoint(nint rootWindow, int x, int y)
    {
        var discovered = WindowFromPoint(new NativePoint(x, y));
        return new VisibleWindowProbe(
            rootWindow,
            discovered,
            discovered == rootWindow || discovered != nint.Zero && IsChild(rootWindow, discovered),
            GetWindowClassName(discovered));
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

    public static bool ApplyVisualRegion(
        nint window,
        int left,
        int top,
        int width,
        int height,
        int topRadius,
        int bottomRadius)
    {
        var region = CreateAsymmetricRoundRectRegion(
            left,
            top,
            width,
            height,
            Math.Max(topRadius, 1),
            bottomRadius);
        if (region == nint.Zero)
        {
            return false;
        }

        if (SetWindowRgn(window, region, true) == 0)
        {
            DeleteObject(region);
            return false;
        }

        return true;
    }

    public static void ApplyEmptyRegion(nint window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region != nint.Zero && SetWindowRgn(window, region, false) == 0)
        {
            DeleteObject(region);
        }
    }

    private static nint CreateAsymmetricRoundRectRegion(
        int left,
        int top,
        int width,
        int height,
        int topRadius,
        int bottomRadius)
    {
        topRadius = Math.Clamp(topRadius, 0, Math.Min(width / 2, height / 2));
        bottomRadius = Math.Clamp(bottomRadius, 0, Math.Min(width / 2, height / 2));
        var destination = CreateRectRgn(
            left,
            top + topRadius,
            left + width + 1,
            Math.Max(top + topRadius + 1, top + height - bottomRadius));
        var topPart = topRadius == 0
            ? CreateRectRgn(left, top, left + width + 1, top + 1)
            : CreateRoundRectRgn(
                left,
                top,
                left + width + 1,
                top + topRadius * 2 + 1,
                topRadius * 2,
                topRadius * 2);
        var bottomPart = bottomRadius == 0
            ? CreateRectRgn(left, top + height - 1, left + width + 1, top + height + 1)
            : CreateRoundRectRgn(
            left,
                Math.Max(top, top + height - bottomRadius * 2),
            left + width + 1,
                top + height + 1,
                bottomRadius * 2,
                bottomRadius * 2);
        if (destination == nint.Zero || topPart == nint.Zero || bottomPart == nint.Zero)
        {
            if (destination != nint.Zero)
            {
                DeleteObject(destination);
            }

            if (topPart != nint.Zero)
            {
                DeleteObject(topPart);
            }

            if (bottomPart != nint.Zero)
            {
                DeleteObject(bottomPart);
            }

            return nint.Zero;
        }

        CombineRgn(destination, destination, topPart, RegionOr);
        CombineRgn(destination, destination, bottomPart, RegionOr);
        DeleteObject(topPart);
        DeleteObject(bottomPart);
        return destination;
    }

    private static nint GetWindowLongPointer(nint window, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, index)
        : new nint(GetWindowLong32(window, index));

    private static nint SetWindowLongPointer(nint window, int index, nint value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(window, index, value)
        : new nint(SetWindowLong32(window, index, value.ToInt32()));

    private static string GetWindowClassName(nint window)
    {
        if (window == nint.Zero)
        {
            return "<none>";
        }

        var value = new System.Text.StringBuilder(256);
        return GetClassName(window, value, value.Capacity) > 0 ? value.ToString() : "<unknown>";
    }

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref uint value,
        int valueSize);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, System.Text.StringBuilder className, int maximumCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

internal sealed record VisibleWindowProbe(
    nint RootWindow,
    nint DiscoveredWindow,
    bool IsRootOrDescendant,
    string WindowClassName);
