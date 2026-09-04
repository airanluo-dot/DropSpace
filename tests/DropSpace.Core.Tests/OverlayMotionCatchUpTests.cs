using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayMotionCatchUpTests
{
    [TestMethod]
    public void OrdinaryDroppedFrameConsumesTheFullBoundedElapsedInterval()
    {
        var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
        controller.SetTarget(new OverlayMotionValues(560, 340, 84, 28, 28, 1, 0, 0, 1, 1), reducedMotion: false);

        controller.Step(TimeSpan.FromMilliseconds(100));

        Assert.IsTrue(controller.Current.Width > OverlayMotionValues.Hidden.Width);
        Assert.IsTrue(controller.Current.IsApiSafe());
    }

    [TestMethod]
    public void ExtremeResumeIntervalFastSettlesToTheAuthorizedTarget()
    {
        var target = new OverlayMotionValues(560, 340, 84, 28, 28, 1, 0, 0, 1, 1);
        var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
        controller.SetTarget(target, reducedMotion: false);

        controller.Step(TimeSpan.FromSeconds(1));

        Assert.IsFalse(controller.IsAnimating);
        Assert.AreEqual(target, controller.Current);
        Assert.IsTrue(controller.Current.IsApiSafe());
    }

    [TestMethod]
    public void NonFiniteElapsedUsesAStableMinimumStep()
    {
        var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
        controller.SetTarget(new OverlayMotionValues(340, 64, 8, 32, 32, 1, 0, 0, 0, 1), reducedMotion: false);

        controller.Step(TimeSpan.FromTicks(long.MaxValue));

        Assert.IsTrue(double.IsFinite(controller.Current.Width));
        Assert.IsTrue(controller.Current.IsApiSafe());
    }
}
