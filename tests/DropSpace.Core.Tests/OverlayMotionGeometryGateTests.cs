using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayMotionGeometryGateTests
{
    [TestMethod]
    public void SettledGeometryLeavesContentMotionActiveUntilTimerFramesFinishIt()
    {
        var profiles = OverlayMotionProfileSet.Default with
        {
            Geometry = new GeometryMorphProfile(
                OverlaySpringProfile.Reduced(0),
                OverlayMotionProfileSet.Default.Geometry.Reduced),
        };
        var compact = Compact();
        var expanded = compact with
        {
            Width = 560,
            Height = 340,
            TopRadius = 28,
            BottomRadius = 28,
            CompactContent = 0,
            ExpandedContent = 1,
        };
        var controller = new OverlayMotionController(compact, profiles);
        controller.SetTarget(expanded, reducedMotion: false);
        Assert.IsTrue(controller.IsGeometryAnimating);

        controller.Step(TimeSpan.FromSeconds(1d / 240));

        Assert.IsFalse(controller.IsGeometryAnimating);
        Assert.IsTrue(controller.IsAnimating);
        Assert.IsTrue(controller.Current.ExpandedContent < expanded.ExpandedContent);

        CompleteWithTimerFrames(controller);

        Assert.IsFalse(controller.IsAnimating);
        Assert.IsFalse(controller.IsGeometryAnimating);
        Assert.AreEqual(expanded, controller.Current);
    }

    [TestMethod]
    public void DropConfirmationKeepsInteractionMotionActiveWithoutGeometryFrames()
    {
        var compact = Compact();
        var controller = new OverlayMotionController(compact);
        controller.PulseDropTarget(OverlayMotionTokens.DropConfirmationScale);

        Assert.IsFalse(controller.IsGeometryAnimating);
        Assert.IsTrue(controller.IsAnimating);
        controller.Step(TimeSpan.FromSeconds(1d / 240));
        Assert.IsFalse(controller.IsGeometryAnimating);
        Assert.IsTrue(controller.IsAnimating);

        CompleteWithTimerFrames(controller);

        Assert.IsFalse(controller.IsAnimating);
        Assert.IsFalse(controller.IsGeometryAnimating);
        Assert.AreEqual(compact, controller.Current);
    }

    private static void CompleteWithTimerFrames(OverlayMotionController controller)
    {
        for (var frame = 0; frame < 600 && controller.IsAnimating; frame++)
            controller.Step(TimeSpan.FromMilliseconds(16));
    }

    private static OverlayMotionValues Compact() => new(340, 64, 8, 32, 32, 1, 1, 0, 0, 1);
}
