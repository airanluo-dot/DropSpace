using System.Runtime.InteropServices;
using DropSpace.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class OverlayRegionNativeSmokeTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    public void ClientSurfaceRegionAccountsForNonClientInset()
    {
        var window = CreateWindowExW(0, "STATIC", "DropSpace region test", 0x00C00000, 200, 200, 300, 200, 0, 0, 0, 0);
        Assert.AreNotEqual(nint.Zero, window);
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            var origin = new Point();
            Assert.IsTrue(ClientToScreen(window, ref origin));
            Assert.IsTrue(GetWindowRect(window, out var bounds));
            var x = origin.X - bounds.Left;
            var y = origin.Y - bounds.Top;
            Assert.IsTrue(y > 0, "This regression needs an actual non-client inset.");
            Assert.IsTrue(OverlayWindowInterop.ApplyVisualRegion(window, 10, 10, 100, 60, 10, 10, out var failure), failure?.ToString());
            Assert.AreNotEqual(0, GetWindowRgn(window, region));
            Assert.IsTrue(PtInRegion(region, x + 60, y + 11));
            Assert.IsFalse(PtInRegion(region, x + 60, y + 9));
            Assert.IsTrue(PtInRegion(region, x + 109, y + 40));
            Assert.IsFalse(PtInRegion(region, x + 110, y + 40));
            Assert.IsFalse(PtInRegion(region, x + 60, y + 70));
        }
        finally { DeleteObject(region); DestroyWindow(window); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}
