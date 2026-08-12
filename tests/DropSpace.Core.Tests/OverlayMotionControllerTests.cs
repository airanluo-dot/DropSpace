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

        Assert.IsTrue(controller.Current.Width > OverlayMotionValues.Hidden.Width);
        Assert.IsTrue(controller.Current.Width < target.Width);
        Assert.IsTrue(controller.Current.Height > OverlayMotionValues.Hidden.Height);
        Assert.IsTrue(controller.Current.Height < target.Height);
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

    [TestMethod]
    public void OneThousandIslandStateReversalsAlwaysProduceApiSafeFrames()
    {
        var compact = IslandCompact();
        var ready = Visible(430, 92, 84, 30, 1);
        var expanded = new OverlayMotionValues(560, 340, 84, 28, 28, 1, 0, 0, 1, 1);
        var controller = new OverlayMotionController(compact);
        for (var cycle = 0; cycle < 1_000; cycle++)
        {
            controller.SetTarget(ready, reducedMotion: false);
            StepAndAssertSafe(controller, 4 + cycle % 7);
            controller.SetTarget(compact, reducedMotion: false);
            StepAndAssertSafe(controller, 2 + cycle % 5);
            controller.SetTarget(expanded, reducedMotion: false);
            StepAndAssertSafe(controller, 1 + cycle % 3);
            controller.SetTarget(compact, reducedMotion: false);
            StepAndAssertSafe(controller, 3 + cycle % 6);
        }

        for (var frame = 0; frame < 600 && controller.IsAnimating; frame++)
        {
            StepAndAssertSafe(controller, 1);
        }

        Assert.IsFalse(controller.IsAnimating);
        Assert.AreEqual(compact, controller.Current);
    }

    [TestMethod]
    public void SafeProjectionClampsNegativeOvershootAndNonFiniteValues()
    {
        var projected = new OverlayMotionValues(
            double.NaN,
            -1,
            -0.5,
            -0.2,
            double.PositiveInfinity,
            1.2,
            -0.1,
            1.1,
            double.NegativeInfinity,
            1.8).ProjectToSafeRange();

        Assert.IsTrue(projected.IsApiSafe());
        Assert.AreEqual(0, projected.TopRadius);
        Assert.AreEqual(1, projected.Opacity);
        Assert.AreEqual(OverlayMotionValues.MaximumDropTargetScale, projected.DropTargetScale);
    }

    private static void StepAndAssertSafe(OverlayMotionController controller, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            controller.Step(TimeSpan.FromMilliseconds(16));
            Assert.IsTrue(controller.Current.IsApiSafe(), $"Unsafe frame: {controller.Current}");
        }
    }

    private static OverlayMotionValues IslandCompact() =>
        new(340, 64, 8, 32, 32, 1, 1, 0, 0, 1);

    private static OverlayMotionValues Visible(
        double width,
        double height,
        double topOffset,
        double radius,
        double dragContent) =>
        new(width, height, topOffset, radius, radius, 1, 0, dragContent, 0, 1);
}
