using System.Runtime.InteropServices;
using DropSpace.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class IslandGlowNativeWindowTests
{
    [TestMethod]
    [TestCategory("WindowsNative")]
    public void CompanionIsOwnedLayeredClickThroughNonActivatingAndFullyTornDown()
    {
        // Stay outside the visible desktop: this tests the real HWND/upload/lifetime
        // contract without showing decorative windows over the CI user's desktop.
        var owner = CreateWindowEx(0x08000080, "STATIC", string.Empty, 0x80000000,
            -30_000, -30_000, 600, 400, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
        Assert.AreNotEqual(nint.Zero, owner);
        var foreground = GetForegroundWindow();
        using var window = new IslandGlowWindow(owner);
        try
        {
            var raster = new IslandGlowRasterizer(280, 60, 26, 26, 1);
            raster.Render(0, 0.7);
            window.Present(raster, 100, 20);
            var handle = window.WindowHandle;
            Assert.AreNotEqual(nint.Zero, handle);
            Assert.AreEqual(owner, GetWindow(handle, 4)); // GW_OWNER
            var style = GetWindowLongPtr(handle, -20).ToInt64(); // GWL_EXSTYLE
            const long required = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;
            Assert.AreEqual(required, style & required);
            Assert.AreEqual(foreground, GetForegroundWindow(), "Glow must never activate itself.");
            Assert.IsTrue(IsWindowVisible(handle));
            Assert.IsTrue(GetWindowRect(handle, out var bounds));
            Assert.AreEqual(raster.Width, bounds.Right - bounds.Left);
            Assert.AreEqual(raster.Height, bounds.Bottom - bounds.Top);
            var point = new NativePoint { X = 100 - raster.PaddingPixels, Y = 20 - raster.PaddingPixels };
            Assert.IsTrue(ClientToScreen(owner, ref point));
            Assert.AreEqual(point.X, bounds.Left);
            Assert.AreEqual(point.Y, bounds.Top);

            window.Hide();
            Assert.IsFalse(IsWindowVisible(handle));
            window.Present(raster, 120, 40);
            Assert.IsTrue(IsWindowVisible(handle));
            Assert.AreEqual(foreground, GetForegroundWindow());
            window.Dispose();
            Assert.IsFalse(IsWindow(handle));
            Assert.AreEqual(nint.Zero, window.WindowHandle);
        }
        finally { _ = DestroyWindow(owner); }
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
