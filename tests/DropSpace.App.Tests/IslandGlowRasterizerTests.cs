using DropSpace.App.Services;
using DropSpace.Core.Overlay;
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
    public void HaloOverlapsOnlyTheInnerEdgeAndLeavesDeepSurfaceAndBitmapBoundaryTransparent(int width, int height, double scale)
    {
        var raster = new IslandGlowRasterizer(width, height, (int)(26 * scale), (int)(26 * scale), scale);
        raster.Render(2.3, 0.8);
        var padding = raster.PaddingPixels;
        var overlap = (int)Math.Ceiling(IslandGlowRasterizer.InnerOverlapDips * scale);
        // A finite overlap under the body closes the old black seam. Deeper surface
        // pixels stay transparent; none of this changes the island's native region.
        for (var x = padding + overlap; x < padding + width - overlap; x++)
            Assert.AreEqual(0, raster.Pixels[(padding + height / 2) * raster.Width + x]);
        for (var y = padding + overlap; y < padding + height - overlap; y++)
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
        // The transition crosses the body edge continuously on all four sides.
        foreach (var distance in new[] { -1d, 0d, 5d })
        {
            var offset = (int)Math.Round(distance * scale);
            Assert.IsGreaterThan(0u, Alpha(raster, padding + width / 2, padding - 1 - offset));
            Assert.IsGreaterThan(0u, Alpha(raster, padding + width / 2, padding + height + offset));
            Assert.IsGreaterThan(0u, Alpha(raster, padding - 1 - offset, padding + height / 2));
            Assert.IsGreaterThan(0u, Alpha(raster, padding + width + offset, padding + height / 2));
        }
        // The default work-area anchor reserves the complete visible halo at this DPI.
        var topPixels = (int)Math.Round(OverlayPlacementPolicy.DynamicIslandTopGapDips * scale);
        for (var y = 0; y < padding - topPixels; y++)
            for (var x = 0; x < raster.Width; x++)
                Assert.AreEqual(0, raster.Pixels[y * raster.Width + x]);
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

    [TestMethod]
    public void RealBandsChangeContourAtFixedPhaseAndEqualBrightness()
    {
        var raster = new IslandGlowRasterizer(280, 60, 26, 26, 1);
        raster.Render(0, 0.4, [0, 0, 0, 0, 0, 0]);
        var quiet = raster.Pixels.ToArray();
        var quietExtent = TopExtent(raster);
        raster.Render(0, 0.4, [1, 1, 1, 1, 1, 1]);
        Assert.IsTrue(TopExtent(raster) > quietExtent + 0.5,
            "Audio must move the alpha contour, not only rotate colors or change brightness.");
        Assert.IsFalse(quiet.SequenceEqual(raster.Pixels));
        raster.Render(0, 0.4, [1, 0, 0, 0, 0, 0]);
        var lowBand = raster.Pixels.ToArray();
        raster.Render(0, 0.4, [0, 0, 0, 0, 0, 1]);
        Assert.IsFalse(lowBand.SequenceEqual(raster.Pixels), "Equal overall energy with different bands must change the local contour.");
    }

    [TestMethod]
    public void InvalidOrMissingBandsCannotInventEnergyOrLeaveResidue()
    {
        var raster = new IslandGlowRasterizer(280, 60, 0, 26, 1.25);
        raster.Render(0, 0.4, [1, 0, 0, 0, 0, 0]);
        var expected = raster.Pixels.ToArray();
        raster.Render(double.NaN, 0.4, [2, double.NaN, -1, double.PositiveInfinity]);
        CollectionAssert.AreEqual(expected, raster.Pixels);
        raster.Render(0, 0.4);
        expected = raster.Pixels.ToArray();
        raster.Render(0, 0.4, []);
        CollectionAssert.AreEqual(expected, raster.Pixels);
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    [DataRow(1.5d)]
    [DataRow(2d)]
    public void AsymmetricAndTinyContoursKeepSignedLookupBounded(double scale)
    {
        foreach (var shape in new[] { (1, 1, 0, 0), (20, 6, 0, 3), (280, 60, 0, 26) })
        {
            var raster = new IslandGlowRasterizer(shape.Item1, shape.Item2, shape.Item3, shape.Item4, scale);
            raster.Render(5, 0.46, [1, 0, 0.5, 1, 0.2, 1]);
            Assert.IsTrue(raster.Pixels.Any(pixel => pixel != 0));
            for (var x = 0; x < raster.Width; x++)
            {
                Assert.AreEqual(0, raster.Pixels[x]);
                Assert.AreEqual(0, raster.Pixels[(raster.Height - 1) * raster.Width + x]);
            }
        }
    }

    private static double TopExtent(IslandGlowRasterizer raster)
    {
        var weight = 0d;
        var distance = 0d;
        for (var y = 0; y < raster.PaddingPixels; y++)
        {
            var alpha = Alpha(raster, raster.Width / 2, y);
            weight += alpha;
            distance += alpha * (raster.PaddingPixels - y - 0.5);
        }
        return distance / weight;
    }

    private static uint Alpha(IslandGlowRasterizer raster, int x, int y) =>
        (uint)raster.Pixels[y * raster.Width + x] >> 24;
}
