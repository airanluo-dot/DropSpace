using System.Reflection;
using System.Runtime.InteropServices;
using DropSpace.App.Services;
using DropSpace.Core.Overlay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

/// <summary>GDI region tests only: no HWND, UI thread, material activation or application launch.</summary>
[TestClass]
public sealed class OverlayRegionRasterizationTests
{
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(2d)]
    public void RegionStaysWithinExclusiveInputBoundsAcrossStatesAndPulse(double dpi)
    {
        foreach (var shape in new[] { (340d, 64d, 32d), (560d, 340d, 28d) })
        foreach (var pulse in new[] { 1d, 0.97, 0.92, 0.75 })
        {
            var values = new OverlayMotionValues(shape.Item1, shape.Item2, 18, shape.Item3, shape.Item3, 1, 1, 0, 0, pulse);
            var signature = OverlayFrameGeometry.Create(values, 600, dpi).Region;
            var region = CreateRegion(signature);
            try
            {
                Assert.AreNotEqual(0, GetRgnBox(region, out var bounds));
                Assert.IsTrue(bounds.Left >= signature.LeftPixels && bounds.Top >= signature.TopPixels);
                Assert.IsTrue(bounds.Right <= signature.LeftPixels + signature.WidthPixels);
                Assert.IsTrue(bounds.Bottom <= signature.TopPixels + signature.HeightPixels);
                var centerX = signature.LeftPixels + signature.WidthPixels / 2;
                var centerY = signature.TopPixels + signature.HeightPixels / 2;
                Assert.IsTrue(PtInRegion(region, centerX, centerY));
                Assert.IsFalse(PtInRegion(region, signature.LeftPixels - 1, centerY));
                Assert.IsFalse(PtInRegion(region, signature.LeftPixels + signature.WidthPixels, centerY));
                Assert.IsFalse(PtInRegion(region, centerX, signature.TopPixels - 1));
                Assert.IsFalse(PtInRegion(region, centerX, signature.TopPixels + signature.HeightPixels));
            }
            finally { Assert.IsTrue(DeleteObject(region)); }
        }
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(2d)]
    public void CoverageRegionIncludesPositiveAreaCornerCellsAndExcludesTangencies(double dpi)
    {
        var signature = OverlayRegionSignature.Create(340, 64, 32, 32, dpi);
        var region = CreateRegion(signature);
        try
        {
            var radius = signature.TopRadiusPixels;
            for (var y = 0; y < radius; y++)
            for (var x = 0; x < radius; x++)
            {
                var dx = radius - x - 1;
                var dy = radius - y - 1;
                var positiveArea = (long)dx * dx + (long)dy * dy < (long)radius * radius;
                Assert.AreEqual(positiveArea, PtInRegion(region, x, y));
                Assert.AreEqual(positiveArea, PtInRegion(region, signature.WidthPixels - x - 1, y));
            }
        }
        finally { Assert.IsTrue(DeleteObject(region)); }
    }

    [TestMethod]
    public void ZeroRadiiProduceExactRectangularBounds()
    {
        var signature = OverlayRegionSignature.Create(5, 7, 100, 60, 0, 0, 1);
        var region = CreateRegion(signature);
        try
        {
            Assert.IsTrue(PtInRegion(region, 5, 7));
            Assert.IsTrue(PtInRegion(region, 104, 66));
            Assert.IsFalse(PtInRegion(region, 105, 66));
            Assert.IsFalse(PtInRegion(region, 104, 67));
        }
        finally { Assert.IsTrue(DeleteObject(region)); }
    }

    [TestMethod]
    [DataRow(340, 64, 32, 32)]
    [DataRow(425, 80, 40, 40)]
    [DataRow(510, 96, 48, 48)]
    [DataRow(680, 128, 64, 64)]
    [DataRow(560, 340, 28, 28)]
    [DataRow(700, 425, 35, 35)]
    [DataRow(840, 510, 42, 42)]
    [DataRow(1120, 680, 56, 56)]
    [DataRow(3, 3, 1, 1)]
    [DataRow(9, 7, 0, 3)]
    [DataRow(9, 7, 3, 0)]
    [DataRow(9, 7, 3, 1)]
    [DataRow(10, 8, 1, 4)]
    [DataRow(10, 8, 4, 1)]
    [DataRow(5, 1, 0, 0)]
    [DataRow(11, 9, 4, 4)]
    public void FullyCoveredFlatBottomRowIsIncludedWithoutExtendingInputBounds(
        int width, int height, int topRadius, int bottomRadius)
    {
        var signature = OverlayRegionSignature.Create(7, -11, width, height, topRadius, bottomRadius, 1);
        var region = CreateRegion(signature);
        try
        {
            Assert.AreNotEqual(0, GetRgnBox(region, out var bounds));
            Assert.AreEqual(signature.TopPixels + height, bounds.Bottom);
            for (var x = signature.BottomRadiusPixels; x < width - signature.BottomRadiusPixels; x++)
            {
                Assert.IsTrue(PtInRegion(region, 7 + x, -11 + height - 1));
                Assert.IsFalse(PtInRegion(region, 7 + x, -11 + height));
            }
            if (signature.BottomRadiusPixels >= 4)
            {
                Assert.IsFalse(PtInRegion(region, 7, -11 + height - 1), "The corner cutout must remain.");
                Assert.IsFalse(PtInRegion(region, 7 + width - 1, -11 + height - 1));
            }
        }
        finally { Assert.IsTrue(DeleteObject(region)); }
    }

    [TestMethod]
    public void CoverageRegionRejectsTangentOnlyCellAndPreservesItsNeighbors()
    {
        var region = CreateRegion(OverlayRegionSignature.Create(16, 14, 5, 5, 1));
        try
        {
            // Pixel (1,0) touches the radius-5 circle only at (2,1): 3^2+4^2=5^2.
            Assert.IsFalse(PtInRegion(region, 1, 0));
            Assert.IsTrue(PtInRegion(region, 2, 0));
            Assert.IsFalse(PtInRegion(region, 14, 0));
            Assert.IsFalse(PtInRegion(region, 1, 13));
            Assert.IsTrue(PtInRegion(region, 2, 13));
        }
        finally { Assert.IsTrue(DeleteObject(region)); }
    }

    private static nint CreateRegion(OverlayRegionSignature signature)
    {
        var create = typeof(OverlayWindowInterop).GetMethod("CreateAsymmetricRoundRectRegion", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(create);
        object?[] arguments = [signature.LeftPixels, signature.TopPixels, signature.WidthPixels, signature.HeightPixels,
            signature.TopRadiusPixels, signature.BottomRadiusPixels, null];
        var region = (nint)create.Invoke(null, arguments)!;
        Assert.AreNotEqual(nint.Zero, region, arguments[6]?.ToString());
        return region;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Rect bounds);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}
