using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DropSpace.App.Services;

/// <summary>
/// UI-thread-owned, visual-only companion to the precisely regioned island HWND.
/// A separate layered window lets light extend beyond that HRGN without enlarging
/// the island's pointer, OLE drop, or activation region. WS_EX_TRANSPARENT on a
/// layered HWND passes input through even to windows owned by another process.
/// </summary>
internal sealed class IslandGlowWindow : IDisposable
{
    private readonly nint _island;
    private nint _window;
    private nint _memoryDc;
    private nint _bitmap;
    private nint _oldBitmap;
    private nint _bits;
    private int _width;
    private int _height;
    private bool _shown;
    private bool _disposed;

    public IslandGlowWindow(nint island) => _island = island;

    internal nint WindowHandle => _window;

    internal bool CanPresent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsWindow(_island))
        {
            // This window is intentionally unowned, so native ownership cannot clean
            // it up for us if the island closes before the controller is disposed.
            Dispose();
            throw new Win32Exception(1400, "The island window has been destroyed.");
        }
        if (!IsWindowVisible(_island) || IsIconic(_island))
        {
            Hide();
            return false;
        }
        return true;
    }

    public void Present(IslandGlowRasterizer rasterizer, int surfaceLeft, int surfaceTop)
    {
        if (!CanPresent()) return;
        EnsureWindow();
        EnsureBitmap(rasterizer.Width, rasterizer.Height);
        Marshal.Copy(rasterizer.Pixels, 0, _bits, rasterizer.Pixels.Length);
        var destination = new NativePoint { X = surfaceLeft, Y = surfaceTop };
        if (!ClientToScreen(_island, ref destination)) ThrowNativeFailure("ClientToScreen(glow)");
        destination.X -= rasterizer.PaddingPixels;
        destination.Y -= rasterizer.PaddingPixels;
        var size = new NativeSize { Width = _width, Height = _height };
        var source = new NativePoint();
        var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!UpdateLayeredWindow(_window, nint.Zero, ref destination, ref size, _memoryDc,
                ref source, 0, ref blend, 2)) ThrowNativeFailure("UpdateLayeredWindow(glow)");
        // An owned popup is always above its owner. This independent visual window is
        // explicitly ordered immediately below the island, without activation or hit testing.
        if (!SetWindowPos(_window, _island, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200 | 0x0040))
            ThrowNativeFailure("SetWindowPos(glow below island)");
        _shown = true;
    }

    public void Hide()
    {
        if (_window == nint.Zero || !_shown) return;
        _ = ShowWindow(_window, 0);
        _shown = false;
    }

    private void EnsureWindow()
    {
        if (_window != nint.Zero) return;
        const uint layered = 0x00080000, transparent = 0x00000020;
        const uint noActivate = 0x08000000, toolWindow = 0x00000080, topmost = 0x00000008;
        // STATIC is a system class: no rooted managed WndProc or background window
        // thread, no XAML/backdrop lifetime, and no independent message pump.
        // Match the island's Z-order band while still hidden. Inserting a normal
        // window after the last topmost window alone would leave it non-topmost.
        var zOrderBand = (uint)(GetWindowLongPtr(_island, -20).ToInt64() & topmost);
        _window = CreateWindowEx(layered | transparent | noActivate | toolWindow | zOrderBand, "STATIC", string.Empty,
            0x80000000, 0, 0, 1, 1, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        if (_window == nint.Zero) ThrowNativeFailure("CreateWindowEx(glow)");
        _memoryDc = CreateCompatibleDC(nint.Zero);
        if (_memoryDc == nint.Zero) ThrowNativeFailure("CreateCompatibleDC(glow)");
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap != nint.Zero && width == _width && height == _height) return;
        ReleaseBitmap();
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, SizeImage = checked((uint)(width * height * 4)),
            },
        };
        _bitmap = CreateDIBSection(_memoryDc, ref info, 0, out _bits, nint.Zero, 0);
        if (_bitmap == nint.Zero || _bits == nint.Zero) ThrowNativeFailure("CreateDIBSection(glow)");
        _oldBitmap = SelectObject(_memoryDc, _bitmap);
        if (_oldBitmap == nint.Zero || _oldBitmap == new nint(-1)) ThrowNativeFailure("SelectObject(glow)");
        _width = width;
        _height = height;
    }

    private void ReleaseBitmap()
    {
        if (_oldBitmap != nint.Zero && _oldBitmap != new nint(-1)) _ = SelectObject(_memoryDc, _oldBitmap);
        _oldBitmap = nint.Zero;
        if (_bitmap != nint.Zero) _ = DeleteObject(_bitmap);
        _bitmap = _bits = nint.Zero;
        _width = _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        ReleaseBitmap();
        if (_memoryDc != nint.Zero) _ = DeleteDC(_memoryDc);
        _memoryDc = nint.Zero;
        if (_window != nint.Zero) _ = DestroyWindow(_window);
        _window = nint.Zero;
        _disposed = true;
    }

    private static void ThrowNativeFailure(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction
    { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, ref NativePoint destination,
        ref NativeSize size, nint sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc,
        ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
}
