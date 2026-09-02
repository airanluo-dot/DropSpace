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
    private const long StylePopup = unchecked((int)0x80000000L);
    private const long StyleChild = 0x40000000L;
    private const long StyleMinimize = 0x20000000L;
    private const long StyleMaximize = 0x01000000L;
    private const long StyleSystemMenu = 0x00080000L;
    private const long StyleHorizontalScroll = 0x00100000L;
    private const long StyleVerticalScroll = 0x00200000L;
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

    public static OverlayNativeConfigurationResult ConfigureVisualWindow(
        nint window,
        bool modernDwmAttributes)
    {
        var failures = new List<OverlayNativeFailure>();
        var criticalFailure = false;
        if (window == nint.Zero || !IsWindow(window))
        {
            failures.Add(new OverlayNativeFailure(
                "IsWindow",
                Critical: true,
                Win32Error: 1400));
            return new OverlayNativeConfigurationResult(false, failures);
        }

        if (!TryGetWindowLongPointer(window, ExtendedStyleIndex, out var extendedStyle, out var extendedGetError))
        {
            failures.Add(new OverlayNativeFailure(
                "GetWindowLongPtrW(extended-style)",
                Critical: true,
                Win32Error: extendedGetError));
            criticalFailure = true;
        }
        else
        {
            var configuredExtendedStyle = extendedStyle.ToInt64() |
                                          ExtendedStyleToolWindow |
                                          ExtendedStyleNoActivate;
            configuredExtendedStyle &= ~(ExtendedStyleAppWindow |
                                         ExtendedStyleClientEdge |
                                         ExtendedStyleWindowEdge |
                                         ExtendedStyleDialogModalFrame);
            if (!TrySetWindowLongPointer(
                    window,
                    ExtendedStyleIndex,
                    new nint(configuredExtendedStyle),
                    out var extendedSetError))
            {
                failures.Add(new OverlayNativeFailure(
                    "SetWindowLongPtrW(extended-style)",
                    Critical: true,
                    Win32Error: extendedSetError));
                criticalFailure = true;
            }
        }

        if (!TryGetWindowLongPointer(window, StyleIndex, out var style, out var styleGetError))
        {
            failures.Add(new OverlayNativeFailure(
                "GetWindowLongPtrW(style)",
                Critical: true,
                Win32Error: styleGetError));
            criticalFailure = true;
        }
        else
        {
            // AppWindow presenters can leave an overlapped/non-client style behind even after
            // SetBorderAndTitleBar(false, false). Force a true popup style so DWM has no frame to
            // paint around the XAML region on either Windows 10 or Windows 11.
            var configuredStyle = style.ToInt64();
            configuredStyle &= ~(StyleBorder |
                                 StyleCaption |
                                 StyleDialogFrame |
                                 StyleThickFrame |
                                 StyleChild |
                                 StyleMinimize |
                                 StyleMaximize |
                                 StyleSystemMenu |
                                 StyleHorizontalScroll |
                                 StyleVerticalScroll);
            configuredStyle |= StylePopup;
            if (!TrySetWindowLongPointer(
                    window,
                    StyleIndex,
                    new nint(configuredStyle),
                    out var styleSetError))
            {
                failures.Add(new OverlayNativeFailure(
                    "SetWindowLongPtrW(style)",
                    Critical: true,
                    Win32Error: styleSetError));
                criticalFailure = true;
            }
        }

        var nonClientPolicy = DwmNonClientRenderingDisabled;
        if (!TrySetDwmAttribute(
                window,
                DwmWindowAttributeNonClientRenderingPolicy,
                ref nonClientPolicy,
                sizeof(int),
                out var nonClientHResult))
        {
            failures.Add(new OverlayNativeFailure(
                "DwmSetWindowAttribute(non-client-policy)",
                Critical: true,
                HResult: nonClientHResult));
            criticalFailure = true;
        }
        if (modernDwmAttributes)
        {
            var cornerPreference = DwmCornerDoNotRound;
            if (!TrySetDwmAttribute(
                    window,
                    DwmWindowAttributeCornerPreference,
                    ref cornerPreference,
                    sizeof(int),
                    out var cornerHResult))
            {
                failures.Add(new OverlayNativeFailure(
                    "DwmSetWindowAttribute(corner-preference)",
                    Critical: false,
                    HResult: cornerHResult));
            }

            var borderColor = DwmColorNone;
            if (!TrySetDwmAttribute(
                    window,
                    DwmWindowAttributeBorderColor,
                    ref borderColor,
                    sizeof(uint),
                    out var borderHResult))
            {
                failures.Add(new OverlayNativeFailure(
                    "DwmSetWindowAttribute(border-color)",
                    Critical: false,
                    HResult: borderHResult));
            }
        }

        if (!TrySetWindowPos(
                window,
                Topmost,
                SetWindowPositionNoMove |
                SetWindowPositionNoSize |
                SetWindowPositionNoActivate |
                SetWindowPositionFrameChanged,
                out var frameError))
        {
            failures.Add(new OverlayNativeFailure(
                "SetWindowPos(frame-changed)",
                Critical: true,
                Win32Error: frameError));
            criticalFailure = true;
        }

        return new OverlayNativeConfigurationResult(!criticalFailure, failures);
    }

    public static bool ShowNoActivateAndTopmost(
        nint window,
        out OverlayNativeFailure? failure)
    {
        if (!IsValidWindow(window, out var invalidWindowFailure))
        {
            failure = invalidWindowFailure;
            return false;
        }

        if (!TrySetWindowPos(
                window,
                Topmost,
                SetWindowPositionNoMove |
                SetWindowPositionNoSize |
                SetWindowPositionNoActivate,
                out var positionError))
        {
            failure = new OverlayNativeFailure("SetWindowPos(show-topmost)", true, positionError);
            return false;
        }

        _ = ShowWindow(window, ShowNoActivate);
        if (!IsWindowVisible(window))
        {
            failure = new OverlayNativeFailure(
                "ShowWindow(SW_SHOWNOACTIVATE)",
                Critical: true,
                Win32Error: Marshal.GetLastWin32Error());
            return false;
        }

        failure = null;
        return true;
    }

    public static bool Hide(nint window, out OverlayNativeFailure? failure)
    {
        if (!IsValidWindow(window, out var invalidWindowFailure))
        {
            failure = invalidWindowFailure;
            return false;
        }

        _ = ShowWindow(window, ShowHide);
        if (IsWindowVisible(window))
        {
            failure = new OverlayNativeFailure(
                "ShowWindow(SW_HIDE)",
                Critical: true,
                Win32Error: Marshal.GetLastWin32Error());
            return false;
        }

        failure = null;
        return true;
    }

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
        var discovered = WindowFromPoint(new NativePoint { X = x, Y = y });
        return new VisibleWindowProbe(
            rootWindow,
            discovered,
            discovered == rootWindow || discovered != nint.Zero && IsChild(rootWindow, discovered),
            GetWindowClassName(discovered));
    }

    public static bool SetNoActivate(
        nint window,
        bool noActivate,
        out OverlayNativeFailure? failure)
    {
        if (!IsValidWindow(window, out var invalidWindowFailure))
        {
            failure = invalidWindowFailure;
            return false;
        }

        if (!TryGetWindowLongPointer(window, ExtendedStyleIndex, out var currentStyle, out var getError))
        {
            failure = new OverlayNativeFailure("GetWindowLongPtrW(extended-style)", true, getError);
            return false;
        }

        var style = currentStyle.ToInt64();
        style = noActivate
            ? style | ExtendedStyleNoActivate
            : style & ~ExtendedStyleNoActivate;
        if (!TrySetWindowLongPointer(window, ExtendedStyleIndex, new nint(style), out var setError))
        {
            failure = new OverlayNativeFailure("SetWindowLongPtrW(extended-style)", true, setError);
            return false;
        }

        if (!TrySetWindowPos(
                window,
                Topmost,
                SetWindowPositionNoMove |
                SetWindowPositionNoSize |
                SetWindowPositionNoActivate |
                SetWindowPositionFrameChanged,
                out var positionError))
        {
            failure = new OverlayNativeFailure("SetWindowPos(frame-changed)", true, positionError);
            return false;
        }

        failure = null;
        return true;
    }

    public static bool ApplyVisualRegion(
        nint window,
        int left,
        int top,
        int width,
        int height,
        int topRadius,
        int bottomRadius,
        out OverlayNativeFailure? failure)
    {
        if (!IsValidWindow(window, out var invalidWindowFailure))
        {
            failure = invalidWindowFailure;
            return false;
        }

        nint region;
        try
        {
            region = CreateAsymmetricRoundRectRegion(
                left,
                top,
                width,
                height,
                Math.Max(topRadius, 1),
                bottomRadius,
                out var creationFailure);
            if (region == nint.Zero)
            {
                failure = creationFailure ?? new OverlayNativeFailure("Create overlay HRGN", true, Marshal.GetLastWin32Error());
                return false;
            }
        }
        catch (OverflowException)
        {
            failure = new OverlayNativeFailure("Create overlay HRGN (geometry overflow)", true, 534);
            return false;
        }

        if (SetWindowRgn(window, region, true) == 0)
        {
            _ = DeleteObject(region);
            failure = new OverlayNativeFailure("SetWindowRgn(visible-region)", true, Marshal.GetLastWin32Error());
            return false;
        }

        // SetWindowRgn transfers ownership to the window after success.
        failure = null;
        return true;
    }

    public static bool ApplyEmptyRegion(nint window, out OverlayNativeFailure? failure)
    {
        if (!IsValidWindow(window, out var invalidWindowFailure))
        {
            failure = invalidWindowFailure;
            return false;
        }

        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == nint.Zero)
        {
            failure = new OverlayNativeFailure("Create empty overlay HRGN", true, Marshal.GetLastWin32Error());
            return false;
        }

        if (SetWindowRgn(window, region, false) == 0)
        {
            _ = DeleteObject(region);
            failure = new OverlayNativeFailure("SetWindowRgn(empty-region)", true, Marshal.GetLastWin32Error());
            return false;
        }

        // SetWindowRgn transfers ownership to the window after success.
        failure = null;
        return true;
    }

    private static nint CreateAsymmetricRoundRectRegion(
        int left,
        int top,
        int width,
        int height,
        int topRadius,
        int bottomRadius,
        out OverlayNativeFailure? failure)
    {
        failure = null;
        if (width <= 0 || height <= 0)
        {
            failure = new OverlayNativeFailure("Validate overlay HRGN geometry", true, 87);
            return nint.Zero;
        }

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
                _ = DeleteObject(bottomPart);
            }

            failure = new OverlayNativeFailure("Create overlay HRGN components", true, Marshal.GetLastWin32Error());
            return nint.Zero;
        }

        if (CombineRgn(destination, destination, topPart, RegionOr) == 0 ||
            CombineRgn(destination, destination, bottomPart, RegionOr) == 0)
        {
            _ = DeleteObject(destination);
            _ = DeleteObject(topPart);
            _ = DeleteObject(bottomPart);
            failure = new OverlayNativeFailure("CombineRgn(overlay-region)", true, Marshal.GetLastWin32Error());
            return nint.Zero;
        }

        _ = DeleteObject(topPart);
        _ = DeleteObject(bottomPart);
        return destination;
    }

    public static bool TryGetClientSize(nint window, out int width, out int height)
    {
        if (!IsWindow(window) || !GetClientRect(window, out var rectangle))
        {
            width = 0;
            height = 0;
            return false;
        }

        width = rectangle.Right - rectangle.Left;
        height = rectangle.Bottom - rectangle.Top;
        return width >= 0 && height >= 0;
    }

    private static bool IsValidWindow(nint window, out OverlayNativeFailure? failure)
    {
        if (window == nint.Zero || !IsWindow(window))
        {
            failure = new OverlayNativeFailure("IsWindow", Critical: true, Win32Error: 1400);
            return false;
        }

        failure = null;
        return true;
    }

    private static bool TryGetWindowLongPointer(
        nint window,
        int index,
        out nint value,
        out int error)
    {
        Marshal.SetLastPInvokeError(0);
        value = IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new nint(GetWindowLong32(window, index));
        error = Marshal.GetLastWin32Error();
        return value != nint.Zero || error == 0;
    }

    private static bool TrySetWindowLongPointer(
        nint window,
        int index,
        nint value,
        out int error)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new nint(SetWindowLong32(window, index, value.ToInt32()));
        error = Marshal.GetLastWin32Error();
        return previous != nint.Zero || error == 0;
    }

    private static bool TrySetWindowPos(
        nint window,
        nint insertAfter,
        uint flags,
        out int error)
    {
        var success = SetWindowPos(window, insertAfter, 0, 0, 0, 0, flags);
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    private static bool TrySetDwmAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize,
        out int hResult)
    {
        try
        {
            hResult = DwmSetWindowAttribute(window, attribute, ref value, valueSize);
            return hResult >= 0;
        }
        catch (DllNotFoundException exception)
        {
            hResult = exception.HResult;
            return false;
        }
        catch (EntryPointNotFoundException exception)
        {
            hResult = exception.HResult;
            return false;
        }
    }

    private static bool TrySetDwmAttribute(
        nint window,
        int attribute,
        ref uint value,
        int valueSize,
        out int hResult)
    {
        try
        {
            hResult = DwmSetWindowAttribute(window, attribute, ref value, valueSize);
            return hResult >= 0;
        }
        catch (DllNotFoundException exception)
        {
            hResult = exception.HResult;
            return false;
        }
        catch (EntryPointNotFoundException exception)
        {
            hResult = exception.HResult;
            return false;
        }
    }

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRectangle rectangle);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal sealed record OverlayNativeFailure(
    string Operation,
    bool Critical,
    int Win32Error = 0,
    int HResult = 0);

internal sealed record OverlayNativeConfigurationResult(
    bool IsSafeToShow,
    IReadOnlyList<OverlayNativeFailure> Failures);

internal sealed record VisibleWindowProbe(
    nint RootWindow,
    nint DiscoveredWindow,
    bool IsRootOrDescendant,
    string WindowClassName);
