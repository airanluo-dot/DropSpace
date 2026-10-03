using System.ComponentModel;
using System.Runtime.InteropServices;
using DropSpace.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class IslandGlowNativeWindowTests
{
    [TestMethod]
    [TestCategory("WindowsNative")]
    public void CachedStaticFrameStillObservesNativeVisibilityAndBodyDestruction()
    {
        var owner = CreateWindowEx(0x08000080, "STATIC", string.Empty, 0x80000000,
            -30_000, -30_000, 400, 200, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        Assert.AreNotEqual(nint.Zero, owner);
        using var window = new IslandGlowWindow(owner);
        try
        {
            var raster = new IslandGlowRasterizer(280, 60, 30, 30, 1);
            raster.Render(1.2, .08);
            Assert.IsFalse(window.CanPresent());
            _ = ShowWindow(owner, 8);
            Assert.IsTrue(window.CanPresent());
            window.Present(raster, 20, 20);
            var handle = window.WindowHandle;
            Assert.IsFalse(raster.Render(1.2, .08));
            _ = ShowWindow(owner, 0);
            Assert.IsFalse(window.CanPresent());
            Assert.IsFalse(IsWindowVisible(handle));
            _ = ShowWindow(owner, 8);
            Assert.IsTrue(window.CanPresent());
            window.Present(raster, 20, 20);
            Assert.IsTrue(IsWindowVisible(handle));
            _ = DestroyWindow(owner); owner = nint.Zero;
            Assert.ThrowsExactly<Win32Exception>(() => window.CanPresent());
            Assert.IsFalse(IsWindow(handle));
        }
        finally { if (owner != nint.Zero) _ = DestroyWindow(owner); }
    }

    [TestMethod]
    [TestCategory("WindowsNative")]
    [DataRow(false)]
    [DataRow(true)]
    public void CompanionStaysBelowBodyWithoutChangingItsRegionAndFullyTearsDown(bool topmost)
    {
        // Stay outside the visible desktop: this tests the real HWND/upload/lifetime
        // contract without showing decorative windows over the CI user's desktop.
        var owner = CreateWindowEx(0x08000080 | (topmost ? 0x00000008u : 0), "STATIC", string.Empty, 0x80000000,
            -30_000, -30_000, 600, 400, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        Assert.AreNotEqual(nint.Zero, owner);
        var foreground = GetForegroundWindow();
        using var window = new IslandGlowWindow(owner);
        try
        {
            var region = CreateRectRgn(100, 20, 380, 80);
            Assert.AreNotEqual(nint.Zero, region);
            Assert.AreNotEqual(0, SetWindowRgn(owner, region, false)); // The system takes ownership.
            _ = ShowWindow(owner, 8); // SW_SHOWNA
            Assert.IsTrue(GetWindowRect(owner, out var originalBounds));
            var raster = new IslandGlowRasterizer(280, 60, 26, 26, 1);
            raster.Render(0, 0.7);
            window.Present(raster, 100, 20);
            var handle = window.WindowHandle;
            Assert.AreNotEqual(nint.Zero, handle);
            Assert.AreEqual(nint.Zero, GetWindow(handle, 4)); // GW_OWNER: owned popups must be above the owner.
            Assert.AreEqual(owner, GetWindow(handle, 3)); // GW_HWNDPREV: body is immediately above glow.
            var style = GetWindowLongPtr(handle, -20).ToInt64(); // GWL_EXSTYLE
            const long required = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
            Assert.AreEqual(required, style & required);
            Assert.AreEqual(topmost, (style & 0x00000008) != 0);
            Assert.AreEqual(foreground, GetForegroundWindow(), "Glow must never activate itself.");
            Assert.IsTrue(IsWindowVisible(handle));
            Assert.IsTrue(GetWindowRect(handle, out var bounds));
            Assert.AreEqual(raster.Width, bounds.Right - bounds.Left);
            Assert.AreEqual(raster.Height, bounds.Bottom - bounds.Top);
            var point = new NativePoint { X = 100 - raster.PaddingPixels, Y = 20 - raster.PaddingPixels };
            Assert.IsTrue(ClientToScreen(owner, ref point));
            Assert.AreEqual(point.X, bounds.Left);
            Assert.AreEqual(point.Y, bounds.Top);
            Assert.IsTrue(GetWindowRect(owner, out var unchangedBounds));
            Assert.AreEqual(originalBounds, unchangedBounds);
            var unchangedRegion = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert.AreNotEqual(0, GetWindowRgn(owner, unchangedRegion));
                Assert.AreNotEqual(0, GetRgnBox(unchangedRegion, out var regionBounds));
                Assert.AreEqual(new NativeRectangle { Left = 100, Top = 20, Right = 380, Bottom = 80 }, regionBounds);
            }
            finally { _ = DeleteObject(unchangedRegion); }

            window.Hide();
            Assert.IsFalse(IsWindowVisible(handle));
            window.Present(raster, 120, 40);
            Assert.IsTrue(IsWindowVisible(handle));
            Assert.AreEqual(owner, GetWindow(handle, 3));
            Assert.AreEqual(foreground, GetForegroundWindow());
            _ = ShowWindow(owner, 0);
            window.Present(raster, 120, 40);
            Assert.IsFalse(IsWindowVisible(handle), "A hidden island must not leave an independent glow window visible.");
            _ = ShowWindow(owner, 8);
            window.Present(raster, 120, 40);
            Assert.IsTrue(IsWindowVisible(handle));
            window.Dispose();
            Assert.IsFalse(IsWindow(handle));
            Assert.AreEqual(nint.Zero, window.WindowHandle);
            Assert.ThrowsExactly<ObjectDisposedException>(() => window.Present(raster, 120, 40));
        }
        finally { _ = DestroyWindow(owner); }
    }

    [TestMethod]
    [TestCategory("WindowsNative")]
    public void UnexpectedBodyDestructionReleasesIndependentWindowOnNextPresentation()
    {
        var owner = CreateWindowEx(0x08000088, "STATIC", string.Empty, 0x80000000,
            -30_000, -30_000, 600, 400, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        Assert.AreNotEqual(nint.Zero, owner);
        using var window = new IslandGlowWindow(owner);
        try
        {
            var raster = new IslandGlowRasterizer(280, 60, 26, 26, 1);
            raster.Render(0, 0.4);
            window.Present(raster, 100, 20);
            Assert.AreEqual(nint.Zero, window.WindowHandle, "A hidden body must not allocate a decorative HWND.");
            _ = ShowWindow(owner, 8);
            window.Present(raster, 100, 20);
            var handle = window.WindowHandle;
            Assert.IsTrue(IsWindow(handle));
            Assert.IsTrue(DestroyWindow(owner));
            owner = nint.Zero;
            Assert.ThrowsExactly<Win32Exception>(() => window.Present(raster, 100, 20));
            Assert.IsFalse(IsWindow(handle));
            Assert.AreEqual(nint.Zero, window.WindowHandle);
        }
        finally { if (owner != nint.Zero) _ = DestroyWindow(owner); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out NativeRectangle rectangle);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
