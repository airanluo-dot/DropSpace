using DropSpace.Core.Models;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayPlacementPolicyTests
{
    [TestMethod]
    public void SmartPlacementUsesOnePhysicalOffsetAcrossDpiScales()
    {
        foreach (var scale in new[] { 1d, 1.25d, 1.5d, 1.75d, 2d })
        {
            var island = OverlayPlacementPolicy.GetTopOffsetDips(
                FileDragWakeMode.SmartExperimental,
                scale);
            Assert.AreEqual(
                OverlayPlacementPolicy.SmartTopOffsetPhysicalPixels +
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
        foreach (var scale in new[] { 1d, 1.25d, 1.5d, 1.75d, 2d })
        {
            var top = OverlayPlacementPolicy.GetTopOffsetDips(
                FileDragWakeMode.SmartExperimental,
                scale);
            Assert.IsTrue(
                top + OverlayPlacementPolicy.MaximumSurfaceHeightDips +
                OverlayPlacementPolicy.HostBottomMarginDips <=
                OverlayPlacementPolicy.MinimumHostHeightDips,
                $"Dynamic Island at {scale:P0} exceeded the visual host.");
        }
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
}
