using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayMotionControllerTests
{
    [TestMethod]
    public void GeometryChangesContinuouslyBeforeReachingTarget()
    {
        var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
        var target = Visible(430, 92, 8, 30, 1);
        controller.SetTarget(target, reducedMotion: false);

        controller.Step(TimeSpan.FromMilliseconds(16));

        Assert.IsGreaterThan(controller.Current.Width, OverlayMotionValues.Hidden.Width);
        Assert.IsLessThan(controller.Current.Width, target.Width);
        Assert.IsGreaterThan(controller.Current.Height, OverlayMotionValues.Hidden.Height);
        Assert.IsLessThan(controller.Current.Height, target.Height);
    }

    [TestMethod]
    public void TargetCanReverseWithoutResettingCurrentGeometry()
    {
        var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
        controller.SetTarget(Visible(560, 340, 8, 28, 1), reducedMotion: false);
        for (var index = 0; index < 8; index++)
        {
            controller.Step(TimeSpan.FromMilliseconds(16));
        }

        var widthAtInterruption = controller.Current.Width;
        controller.SetTarget(OverlayMotionValues.Hidden, reducedMotion: false);

        Assert.AreEqual(widthAtInterruption, controller.Current.Width, 0.0001);
        controller.Step(TimeSpan.FromMilliseconds(16));
        Assert.AreNotEqual(OverlayMotionValues.Hidden.Width, controller.Current.Width);
    }

    [TestMethod]
    public void EveryAnimatedPropertySettlesAndStopsFrames()
    {
        var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
        var target = new OverlayMotionValues(560, 340, 8, 28, 28, 1, 0, 0, 1, 1);
        controller.SetTarget(target, reducedMotion: false);

        for (var index = 0; index < 600 && controller.IsAnimating; index++)
        {
            controller.Step(TimeSpan.FromMilliseconds(16));
        }

        Assert.IsFalse(controller.IsAnimating);
        Assert.AreEqual(target, controller.Current);
    }

    [TestMethod]
    public void OneHundredInterruptibleCyclesReturnToIdle()
    {
        var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
        for (var cycle = 0; cycle < 100; cycle++)
        {
            controller.SetTarget(Visible(430, 92, 8, 30, 1), reducedMotion: false);
            controller.Step(TimeSpan.FromMilliseconds(16));
            controller.SetTarget(Visible(340, 64, 8, 32, 1), reducedMotion: false);
            controller.Step(TimeSpan.FromMilliseconds(16));
            controller.SetTarget(OverlayMotionValues.Hidden, reducedMotion: false);
            while (controller.IsAnimating)
            {
                controller.Step(TimeSpan.FromMilliseconds(16));
            }
        }

        Assert.AreEqual(OverlayMotionValues.Hidden, controller.Current);
        Assert.IsFalse(controller.IsAnimating);
    }

    private static OverlayMotionValues Visible(
        double width,
        double height,
        double topOffset,
        double radius,
        double dragContent) =>
        new(width, height, topOffset, radius, radius, 1, 0, dragContent, 0, 1);
}
