using DropSpace.Core.Models;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayPlacementPolicyTests
{
    [TestMethod]
    public void DefaultAnchorAndContentFitAcrossAspectRatiosDpiAndWorkAreaOrigins()
    {
        foreach (var screen in new[] { (1920,1080), (2560,1440), (3840,2160), (1080,1920), (3440,1440), (800,480) })
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2, 3 })
        foreach (var origin in new[] { (0,0), (-3840,0), (1920,48) })
        {
            var request = new OverlayPlacementRequest(origin.Item1, origin.Item2, screen.Item1, screen.Item2, dpi, FileDragWakeMode.SmartExperimental);
            var resolved = OverlayPlacementPolicy.Resolve(request, OverlayPlacementMode.Automatic, null);
            var center = resolved.HostLeftPixels + OverlayPlacementPolicy.HostWidthDips * dpi / 2;
            Assert.AreEqual(origin.Item1 + screen.Item1 / 2d, center, 0.51);
            Assert.AreEqual(origin.Item2, resolved.HostTopPixels);
            Assert.AreEqual(OverlayPlacementPolicy.DynamicIslandTopGapDips, resolved.SurfaceTopOffsetDips);
            var scale = OverlayPlacementPolicy.FitContentScale(screen.Item1, screen.Item2, dpi, 560, 340, 2);
            Assert.IsTrue(scale > 0);
            Assert.IsTrue(560 * scale * dpi <= screen.Item1);
            Assert.IsTrue((340 * scale + resolved.SurfaceTopOffsetDips) * dpi <= screen.Item2);
        }
    }

    [TestMethod]
    public void DoubleSizedContentKeepsCustomCenterAndBoundsAcrossDpi()
    {
        foreach (var dpi in new[] { 1d, 1.25d, 2d })
        {
            var request = new OverlayPlacementRequest(0, 0, (int)(1920 * dpi), (int)(1080 * dpi), dpi, FileDragWakeMode.Disabled, 2);
            var resolved = OverlayPlacementPolicy.Resolve(request, new OverlayMonitorPlacement(OverlayPlacementMode.Custom, 960, 200));
            var projected = OverlayPlacementPolicy.ProjectResolvedPlacement(resolved, 0, 0, dpi, 2);
            Assert.AreEqual(960, projected.X, 0.001);
            Assert.AreEqual(200, projected.Y, 0.001);
            var edge = OverlayPlacementPolicy.Resolve(request, new OverlayMonitorPlacement(OverlayPlacementMode.Custom, 1920, 1080));
            var bounded = OverlayPlacementPolicy.ProjectResolvedPlacement(edge, 0, 0, dpi, 2);
            Assert.AreEqual(1360, bounded.X, 0.001);
            Assert.AreEqual(400, bounded.Y, 0.001);
            Assert.IsTrue(OverlayPlacementPolicy.GetMinimumHostHeightDips(dpi, 2) > 680);
        }
    }

    [TestMethod]
    public void SmartPlacementKeepsSmallVisualGapAcrossDpiScales()
    {
        foreach (var scale in new[] { 1d, 1.25d, 1.5d, 1.75d, 2d })
        {
            var island = OverlayPlacementPolicy.GetTopOffsetDips(
                FileDragWakeMode.SmartExperimental,
                scale);
            Assert.AreEqual(
                OverlayPlacementPolicy.DynamicIslandTopGapDips * scale,
                island * scale,
                0.001);
        }
    }

    [TestMethod]
    public void ClassicAndDisabledModesKeepTheirTopEdgeAnchor()
    {
        foreach (var wakeMode in new[] { FileDragWakeMode.ClassicTopEdge, FileDragWakeMode.Disabled })
        {
            Assert.AreEqual(
                OverlayPlacementPolicy.DynamicIslandTopGapDips,
                OverlayPlacementPolicy.GetTopOffsetDips(
                    wakeMode,
                    1.5d));
        }
    }

    [TestMethod]
    public void SmartExpandedSurfaceFitsInsideTheFixedHostAtSupportedDpiScales()
    {
        foreach (var scale in new[] { 0.8d, 1d, 1.25d, 1.5d, 1.75d, 2d })
        {
            var top = OverlayPlacementPolicy.GetTopOffsetDips(
                FileDragWakeMode.SmartExperimental,
                scale);
            Assert.IsTrue(
                top + OverlayPlacementPolicy.MaximumSurfaceHeightDips +
                OverlayPlacementPolicy.HostBottomMarginDips <=
                OverlayPlacementPolicy.GetMinimumHostHeightDips(scale),
                $"Dynamic Island at {scale:P0} exceeded the visual host.");
        }
    }

    [TestMethod]
    public void ScaleAwareHostHeightRetainsTheLegacyOneToOneValue()
    {
        Assert.AreEqual(
            OverlayPlacementPolicy.MinimumHostHeightDips,
            OverlayPlacementPolicy.GetMinimumHostHeightDips(1),
            0.001);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            OverlayPlacementPolicy.GetMinimumHostHeightDips(0));
    }

    [TestMethod]
    public void InvalidScaleIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            OverlayPlacementPolicy.GetTopOffsetDips(
                FileDragWakeMode.SmartExperimental,
                0));
    }

    [TestMethod]
    public void CustomPlacementClampsTransientlyWithoutMutatingSavedCoordinates()
    {
        var saved = new OverlayCustomPlacement(-500, 9_000);
        var resolved = OverlayPlacementPolicy.Resolve(
            new OverlayPlacementRequest(-1920, 0, 1920, 1080, 1.5, FileDragWakeMode.SmartExperimental),
            OverlayPlacementMode.Custom,
            saved);

        Assert.IsTrue(resolved.WasClamped);
        Assert.AreEqual(-500, saved.X);
        Assert.AreEqual(9_000, saved.Y);
        Assert.IsTrue(resolved.HostLeftPixels >= -1920 - (int)(OverlayPlacementPolicy.HostWidthDips * 1.5 / 2));
        Assert.AreEqual(0, resolved.SurfaceTopOffsetDips);
    }

    [TestMethod]
    public void UnconfiguredMonitorPlacementRemainsAutomaticWithoutGlobalFallbackCoordinates()
    {
        var automatic = OverlayPlacementPolicy.Resolve(
            new OverlayPlacementRequest(0, 0, 1920, 1080, 1.5, FileDragWakeMode.ClassicTopEdge),
            new OverlayMonitorPlacement(OverlayPlacementMode.Automatic, 300, 8));

        var expected = OverlayPlacementPolicy.Resolve(
            new OverlayPlacementRequest(0, 0, 1920, 1080, 1.5, FileDragWakeMode.ClassicTopEdge),
            new OverlayMonitorPlacement(OverlayPlacementMode.Automatic, 0, 0));

        Assert.AreEqual(expected, automatic);
        Assert.AreEqual(OverlayPlacementPolicy.DynamicIslandTopGapDips, automatic.SurfaceTopOffsetDips);
    }

    [TestMethod]
    public void EveryVisualStateCanReuseOneResolvedAnchor()
    {
        var request = new OverlayPlacementRequest(0, 40, 2560, 1400, 2, FileDragWakeMode.Disabled);
        var placement = OverlayPlacementPolicy.Resolve(
            request,
            OverlayPlacementMode.Custom,
            new OverlayCustomPlacement(640, 24));

        Assert.AreEqual(40 + 48, placement.HostTopPixels);
        Assert.AreEqual(0, placement.SurfaceTopOffsetDips);
        Assert.IsFalse(placement.WasClamped);
    }

    [TestMethod]
    public void AutomaticSmartProjectionIncludesVisibleSurfaceOffset()
    {
        var projected = OverlayPlacementPolicy.ProjectResolvedPlacement(
            new OverlayResolvedPlacement(300, 0, 84, false),
            0,
            0,
            1);

        Assert.AreEqual(84, projected.Y, 0.001);
    }

    [TestMethod]
    public void ProjectionPreservesDpiScaledSurfaceOffsetInDipCoordinates()
    {
        var projected = OverlayPlacementPolicy.ProjectResolvedPlacement(
            new OverlayResolvedPlacement(300, 150, 8 + 76d / 1.5, false),
            0,
            0,
            1.5);

        Assert.AreEqual(8 + 76d / 1.5 + 100, projected.Y, 0.001);
    }

    [TestMethod]
    public void CustomProjectionDoesNotAddAnExtraSurfaceOffset()
    {
        var projected = OverlayPlacementPolicy.ProjectResolvedPlacement(
            new OverlayResolvedPlacement(300, 180, 0, false),
            0,
            0,
            1.5);

        Assert.AreEqual(120, projected.Y, 0.001);
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    [DataRow(1.5d)]
    [DataRow(2d)]
    public void InvisibleRevealUsesResolvedAnchorWithoutChangingGenericHiddenOrCustomPosition(double dpi)
    {
        var request = new OverlayPlacementRequest(-1920, 48, 1920, 1080, dpi, FileDragWakeMode.Disabled);
        var automatic = OverlayPlacementPolicy.Resolve(request, OverlayPlacementMode.Automatic, null);
        var anchored = OverlayPlacementPolicy.AnchorInvisibleSurface(OverlayMotionValues.Hidden, automatic);
        Assert.AreEqual(OverlayPlacementPolicy.DynamicIslandTopGapDips, anchored.TopOffset);
        Assert.AreEqual(OverlayMotionValues.Hidden with { TopOffset = automatic.SurfaceTopOffsetDips }, anchored);
        Assert.AreEqual(0d, OverlayMotionValues.Hidden.TopOffset, "Hidden must remain placement-independent.");
        foreach (var customY in new[] { 0d, 24d, 80d })
        {
            var custom = OverlayPlacementPolicy.Resolve(request, OverlayPlacementMode.Custom, new(400, customY));
            var customPose = OverlayPlacementPolicy.AnchorInvisibleSurface(anchored, custom);
            Assert.AreEqual(0d, customPose.TopOffset, "Custom placement is in the host origin, not a fixed 18-DIP pose offset.");
            Assert.AreEqual(48 + (int)Math.Round(customY * dpi), custom.HostTopPixels);
            Assert.AreEqual(anchored.Opacity, customPose.Opacity);
            Assert.AreEqual(anchored.Width, customPose.Width);
            Assert.AreEqual(anchored.Height, customPose.Height);
        }
    }

    [TestMethod]
    public void AnchoringDoesNotResetVisibleFadeOrReversal()
    {
        var placement = new OverlayResolvedPlacement(0, 0, 18, false);
        var visible = new OverlayMotionValues(280, 60, 11, 26, 26, 0.25, 0.2, 0, 0, 0.98);
        Assert.AreEqual(visible, OverlayPlacementPolicy.AnchorInvisibleSurface(visible, placement));
        var fading = visible with { Opacity = 0.002 };
        Assert.AreEqual(fading, OverlayPlacementPolicy.AnchorInvisibleSurface(fading, placement));
        var invisible = visible with { Opacity = 0 };
        Assert.AreEqual(invisible with { TopOffset = 18 }, OverlayPlacementPolicy.AnchorInvisibleSurface(invisible, placement));
    }
}
