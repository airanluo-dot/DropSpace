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
            var notch = OverlayPlacementPolicy.GetTopOffsetDips(
                OverlayDisplayMode.Notch,
                FileDragWakeMode.SmartExperimental,
                scale);
            var island = OverlayPlacementPolicy.GetTopOffsetDips(
                OverlayDisplayMode.DynamicIsland,
                FileDragWakeMode.SmartExperimental,
                scale);

            Assert.AreEqual(
                OverlayPlacementPolicy.SmartTopOffsetPhysicalPixels,
                notch * scale,
                0.001);
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
            Assert.AreEqual(0d, OverlayPlacementPolicy.GetTopOffsetDips(
                OverlayDisplayMode.Notch,
                wakeMode,
                1.5d));
            Assert.AreEqual(
                OverlayPlacementPolicy.DynamicIslandTopGapDips,
                OverlayPlacementPolicy.GetTopOffsetDips(
                    OverlayDisplayMode.DynamicIsland,
                    wakeMode,
                    1.5d));
        }
    }

    [TestMethod]
    public void SmartExpandedSurfaceFitsInsideTheFixedHostAtSupportedDpiScales()
    {
        foreach (var scale in new[] { 1d, 1.25d, 1.5d, 1.75d, 2d })
        {
            foreach (var displayMode in Enum.GetValues<OverlayDisplayMode>())
            {
                var top = OverlayPlacementPolicy.GetTopOffsetDips(
                    displayMode,
                    FileDragWakeMode.SmartExperimental,
                    scale);
                Assert.IsTrue(
                    top + OverlayPlacementPolicy.MaximumSurfaceHeightDips +
                    OverlayPlacementPolicy.HostBottomMarginDips <=
                    OverlayPlacementPolicy.MinimumHostHeightDips,
                    $"{displayMode} at {scale:P0} exceeded the visual host.");
            }
        }
    }

    [TestMethod]
    public void InvalidScaleIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            OverlayPlacementPolicy.GetTopOffsetDips(
                OverlayDisplayMode.DynamicIsland,
                FileDragWakeMode.SmartExperimental,
                0));
    }
}
