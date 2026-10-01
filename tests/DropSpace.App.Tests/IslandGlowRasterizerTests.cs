using DropSpace.App.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class IslandGlowRasterizerTests
{
    [TestMethod]
    [DataRow(280, 60, 1d)]
    [DataRow(560, 340, 1d)]
    [DataRow(700, 425, 1.25d)]
    [DataRow(840, 510, 1.5d)]
    [DataRow(1120, 680, 2d)]
    public void HaloLeavesSurfaceAndEntireBitmapBoundaryTransparent(int width, int height, double scale)
    {
        var raster = new IslandGlowRasterizer(width, height, (int)(26 * scale), (int)(26 * scale), scale);
        raster.Render(2.3, 0.8);
        var padding = raster.PaddingPixels;
        // The centre axes lie inside the contour, regardless of rounded corner radii.
        for (var x = padding; x < padding + width; x++)
            Assert.AreEqual(0, raster.Pixels[(padding + height / 2) * raster.Width + x]);
        for (var y = padding; y < padding + height; y++)
            Assert.AreEqual(0, raster.Pixels[y * raster.Width + padding + width / 2]);
        for (var x = 0; x < raster.Width; x++)
        {
            Assert.AreEqual(0, raster.Pixels[x]);
            Assert.AreEqual(0, raster.Pixels[(raster.Height - 1) * raster.Width + x]);
        }
        for (var y = 0; y < raster.Height; y++)
        {
            Assert.AreEqual(0, raster.Pixels[y * raster.Width]);
            Assert.AreEqual(0, raster.Pixels[y * raster.Width + raster.Width - 1]);
        }
        // All four sides contain broad exterior light, not only a 1–2 px outline.
        var spread = (int)(15 * scale);
        Assert.IsGreaterThan(12u, Alpha(raster, padding + width / 2, padding - spread));
        Assert.IsGreaterThan(12u, Alpha(raster, padding + width / 2, padding + height + spread));
        Assert.IsGreaterThan(12u, Alpha(raster, padding - spread, padding + height / 2));
        Assert.IsGreaterThan(12u, Alpha(raster, padding + width + spread, padding + height / 2));
    }

    [TestMethod]
    public void PixelsArePremultipliedAndFadeHasNoColoredResidue()
    {
        var raster = new IslandGlowRasterizer(280, 60, 26, 26, 1);
        foreach (var brightness in new[] { 0.8, 0.4, 0.01, 0d })
        {
            raster.Render(1.2, brightness);
            foreach (var pixel in raster.Pixels)
            {
                var alpha = (uint)pixel >> 24;
                Assert.IsTrue(((pixel >> 16) & 255) <= alpha);
                Assert.IsTrue(((pixel >> 8) & 255) <= alpha);
                Assert.IsTrue((pixel & 255) <= alpha);
                if (alpha == 0) Assert.AreEqual(0, pixel);
            }
        }
        Assert.IsTrue(raster.Pixels.All(pixel => pixel == 0));
    }

    [TestMethod]
    public void PhaseMovesLightWithoutMovingContourAndSamePhaseIsStable()
    {
        var raster = new IslandGlowRasterizer(560, 340, 28, 28, 1);
        raster.Render(0, 0.7);
        var first = raster.Pixels.ToArray();
        raster.Render(0, 0.7);
        CollectionAssert.AreEqual(first, raster.Pixels, "Reduced-motion phase must remain stable.");
        raster.Render(3, 0.7);
        Assert.IsFalse(first.SequenceEqual(raster.Pixels));
        var center = (raster.PaddingPixels + 170) * raster.Width + raster.PaddingPixels + 280;
        Assert.AreEqual(0, first[center]);
        Assert.AreEqual(0, raster.Pixels[center]);
    }

    [TestMethod]
    public void ZeroAndInvalidBrightnessAreAlwaysCompletelyTransparent()
    {
        var raster = new IslandGlowRasterizer(280, 60, 0, 26, 1);
        foreach (var brightness in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
        {
            raster.Render(4, 0.8);
            raster.Render(4, brightness);
            Assert.IsTrue(raster.Pixels.All(pixel => pixel == 0));
        }
    }

    private static uint Alpha(IslandGlowRasterizer raster, int x, int y) =>
        (uint)raster.Pixels[y * raster.Width + x] >> 24;
}
