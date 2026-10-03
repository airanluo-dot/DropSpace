using DropSpace.Core.Island;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayFrameGeometryTests
{
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(2d)]
    public void TransformedXamlBoundsAndRadiiMatchNativePixels(double dpiScale)
    {
        foreach (var state in new[] { OverlayState.Compact, OverlayState.Expanded, OverlayState.DragApproaching, OverlayState.DragReady })
        foreach (var pulse in new[] { 1d, 0.97, 0.92, 0.75 })
        {
            var values = Visible(state) with { DropTargetScale = pulse };
            var geometry = OverlayFrameGeometry.Create(values, 600, dpiScale);
            AssertAligned(geometry, 600);
            // The first measured root width can itself differ from the requested DIP host.
            AssertAligned(geometry, 600 + 0.4 / dpiScale);
            AssertInsideHost(geometry.Region, dpiScale);
        }
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(2d)]
    public void ExistingInputBoundingRectangleIsPreserved(double dpiScale)
    {
        foreach (var state in new[] { OverlayState.Compact, OverlayState.Expanded })
        foreach (var pulse in new[] { 1d, 0.97, 0.92, 0.75 })
        {
            var values = Visible(state) with { DropTargetScale = pulse };
            var geometry = OverlayFrameGeometry.Create(values, 600, dpiScale);
            Assert.AreEqual((int)Math.Round(values.Width * pulse * dpiScale), geometry.Region.WidthPixels);
            Assert.AreEqual((int)Math.Round(values.Height * pulse * dpiScale), geometry.Region.HeightPixels);
            Assert.AreEqual(((int)Math.Round(600 * dpiScale) - geometry.Region.WidthPixels) / 2, geometry.Region.LeftPixels);
            Assert.AreEqual((int)Math.Round((values.TopOffset + values.Height * (1 - pulse) / 2) * dpiScale), geometry.Region.TopPixels);
        }
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(2d)]
    public void FractionalSpringFramesAndAcceptedDropReturnRemainAligned(double dpiScale)
    {
        var controller = new OverlayMotionController(Visible(OverlayState.Compact));
        controller.SetTarget(Visible(OverlayState.Expanded), reducedMotion: false);
        for (var frame = 0; frame < 240; frame++)
        {
            if (frame == 8) controller.SetTarget(Visible(OverlayState.Compact), reducedMotion: false);
            if (frame == 20) controller.SetTarget(Visible(OverlayState.Expanded), reducedMotion: false);
            if (frame == 75) controller.PulseDropTarget(0.97);
            controller.Step(TimeSpan.FromMilliseconds(16));
            var geometry = OverlayFrameGeometry.Create(controller.Current, 600, dpiScale);
            AssertAligned(geometry, 600);
            AssertInsideHost(geometry.Region, dpiScale);
        }
        Assert.IsFalse(controller.IsAnimating);
        Assert.AreEqual(1d, controller.Current.DropTargetScale);
    }

    [TestMethod]
    public void HalfPixelTiesUseHostRoundingAndOddCenterIsCompensated()
    {
        var signature = OverlayRegionSignature.Create(340, 64, 30, 30, 1.25);
        Assert.AreEqual(38, signature.TopRadiusPixels);
        var tie = OverlayRegionSignature.Create(340, 64, 26, 26, 1.25);
        Assert.AreEqual(32, tie.TopRadiusPixels, "32.5 uses the host's ToEven policy.");
        var oddCenter = OverlayFrameGeometry.Create(Visible(OverlayState.Compact), 600, 1.25);
        Assert.AreEqual(425, oddCenter.Region.WidthPixels);
        Assert.AreEqual(162, oddCenter.Region.LeftPixels);
        Assert.AreEqual(-0.4, oddCenter.TranslationXDips(600), 1e-10);
        AssertAligned(oddCenter, 600);
    }

    [TestMethod]
    public void RadiiIncludePulseAndNativeClampForOddCapsuleHeight()
    {
        var pulse = OverlayFrameGeometry.Create(Visible(OverlayState.Expanded) with { DropTargetScale = 0.97 }, 600, 2);
        Assert.AreEqual(54, pulse.Region.TopRadiusPixels, "28 * .97 * 2 rounds to 54, rather than the old unscaled 56.");
        var odd = OverlayFrameGeometry.Create(Visible(OverlayState.Compact) with { Height = 65, TopRadius = 32.5, BottomRadius = 32.5 }, 600, 1.25);
        Assert.AreEqual(81, odd.Region.HeightPixels);
        Assert.AreEqual(40, odd.Region.TopRadiusPixels, "The native builder clamps against integer height / 2.");
        AssertAligned(odd, 600);
    }

    [TestMethod]
    public void SurfaceOvershootDoesNotExpandTheHostAndZeroRadiusStaysZero()
    {
        var geometry = OverlayFrameGeometry.Create(Visible(OverlayState.Expanded) with { DropTargetScale = 1.03, TopRadius = 0, BottomRadius = 0 }, 600, 1.5);
        Assert.AreEqual(1d, geometry.SurfaceScale);
        Assert.AreEqual(840, geometry.Region.WidthPixels);
        Assert.AreEqual(0, geometry.Region.TopRadiusPixels);
        Assert.AreEqual(0d, geometry.TopRadiusDips);
        AssertInsideHost(geometry.Region, 1.5);
    }

    private static OverlayMotionValues Visible(OverlayState state)
    {
        var shape = IslandGeometry.ForFiles(state);
        return new OverlayMotionValues(shape.Width, shape.Height, 18, shape.Radius, shape.Radius, 1, 1, 0, 0, 1);
    }

    private static void AssertAligned(OverlayFrameGeometry geometry, double rootWidthDips)
    {
        var dpi = geometry.DpiScale;
        var scale = geometry.SurfaceScale;
        var region = geometry.Region;
        var transformedLeft = rootWidthDips / 2 - geometry.WidthDips * scale / 2 + geometry.TranslationXDips(rootWidthDips);
        var transformedTop = geometry.HeightDips * (1 - scale) / 2 + geometry.TranslationYDips;
        Assert.AreEqual((double)region.LeftPixels, transformedLeft * dpi, 1e-9);
        Assert.AreEqual((double)region.TopPixels, transformedTop * dpi, 1e-9);
        Assert.AreEqual((double)region.WidthPixels, geometry.WidthDips * scale * dpi, 1e-9);
        Assert.AreEqual((double)region.HeightPixels, geometry.HeightDips * scale * dpi, 1e-9);
        Assert.AreEqual((double)region.TopRadiusPixels, geometry.TopRadiusDips * scale * dpi, 1e-9);
        Assert.AreEqual((double)region.BottomRadiusPixels, geometry.BottomRadiusDips * scale * dpi, 1e-9);
    }

    private static void AssertInsideHost(OverlayRegionSignature region, double dpi)
    {
        Assert.IsTrue(region.LeftPixels >= 0 && region.TopPixels >= 0);
        Assert.IsTrue(region.LeftPixels + region.WidthPixels <= (int)Math.Round(600 * dpi));
        Assert.IsTrue(region.TopPixels + region.HeightPixels <= (int)Math.Round(374 * dpi));
    }
}
