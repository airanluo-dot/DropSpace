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
}
