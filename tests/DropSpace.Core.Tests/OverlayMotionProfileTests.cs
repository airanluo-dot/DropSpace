using DropSpace.Core.Models;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayMotionProfileTests
{
    [TestMethod]
    public void DefaultProfilesKeepVisualChannelsSeparate()
    {
        var profiles = OverlayMotionProfileSet.Default;

        Assert.IsTrue(profiles.IsValid);
        Assert.AreNotEqual(
            profiles.Geometry.Normal.AngularFrequency,
            profiles.SurfaceOpacity.Normal.AngularFrequency);
        Assert.AreNotEqual(
            profiles.SurfaceOpacity.Normal.RestDistance,
            profiles.ContentTransition.Normal.RestDistance);
        Assert.AreEqual(42, profiles.ContentTransition.IncomingDelayMilliseconds);
        Assert.AreEqual(OverlayMotionTokens.DropConfirmationScale, profiles.InteractionFeedback.DropConfirmationScale);
    }

    [TestMethod]
    public void ReducedMotionDoesNotOvershootGeometryOrScale()
    {
        var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
        var target = new OverlayMotionValues(560, 340, 8, 28, 28, 1, 0, 0, 1, 1);
        controller.SetTarget(target, reducedMotion: true);

        for (var frame = 0; frame < 120 && controller.IsAnimating; frame++)
        {
            controller.Step(TimeSpan.FromMilliseconds(16));
            Assert.IsTrue(controller.Current.Width <= target.Width);
            Assert.IsTrue(controller.Current.Height <= target.Height);
            Assert.IsTrue(controller.Current.DropTargetScale <= 1.0);
            Assert.IsTrue(controller.Current.IsApiSafe());
        }

        Assert.AreEqual(target, controller.Current);
    }

    [TestMethod]
    public void DropConfirmationReturnsInwardWithoutOutwardExpansion()
    {
        var controller = new OverlayMotionController(new OverlayMotionValues(
            340, 64, 8, 32, 32, 1, 1, 0, 0, 1, 1));
        controller.SetTarget(new OverlayMotionValues(
            340, 64, 8, 32, 32, 1, 1, 0, 0, 1, 1), reducedMotion: false);
        controller.PulseDropTarget(OverlayMotionTokens.DropConfirmationScale);

        var sawInward = false;
        for (var frame = 0; frame < 240 && controller.IsAnimating; frame++)
        {
            controller.Step(TimeSpan.FromMilliseconds(16));
            sawInward |= controller.Current.DropTargetScale < 1;
            Assert.IsTrue(controller.Current.DropTargetScale <= OverlayMotionValues.MaximumDropTargetScale);
        }

        Assert.IsTrue(sawInward);
        Assert.AreEqual(1, controller.Current.DropTargetScale, 0.0001);
    }

    [TestMethod]
    public void DisplayCadenceAndDroppedFramesRemainApiSafe()
    {
        var cadenceMilliseconds = new[] { 1000d / 60, 1000d / 120, 1000d / 144, 100d };
        var target = new OverlayMotionValues(560, 340, 84, 28, 28, 1, 0, 0, 1, 1);

        foreach (var cadence in cadenceMilliseconds)
        {
            var controller = new OverlayMotionController(OverlayMotionValues.Hidden);
            controller.SetTarget(target, reducedMotion: false);

            for (var frame = 0; frame < 2_400 && controller.IsAnimating; frame++)
            {
                controller.Step(TimeSpan.FromMilliseconds(cadence));
                Assert.IsTrue(controller.Current.IsApiSafe(), $"Unsafe frame at {cadence:F3} ms.");
            }

            Assert.IsFalse(controller.IsAnimating, $"Controller did not settle at {cadence:F3} ms.");
            Assert.AreEqual(target, controller.Current);
        }
    }

    [TestMethod]
    public void RegionPolicySkipsIdenticalPhysicalSignatures()
    {
        var signature = OverlayRegionSignature.Create(340, 64, 32, 32, 1.25);
        var policy = new OverlayRegionUpdatePolicy();

        Assert.IsTrue(policy.ShouldApply(signature));
        Assert.IsFalse(policy.ShouldApply(signature));
        Assert.IsFalse(policy.ShouldApply(signature));
        Assert.AreEqual(1, policy.ApplyAttempts);
        Assert.AreEqual(2, policy.SkippedUpdates);
        Assert.AreEqual(signature, policy.LastApplied);
    }

    [TestMethod]
    public void RegionSignatureIncludesDpiAndShapeMode()
    {
        var at100 = OverlayRegionSignature.Create(340, 64, 32, 32, 1);
        var at125 = OverlayRegionSignature.Create(340, 64, 32, 32, 1.25);
        var empty = OverlayRegionSignature.Empty;

        Assert.AreNotEqual(at100, at125);
        Assert.AreEqual(OverlayShapeMode.Empty, empty.ShapeMode);
        Assert.IsTrue(empty.IsEmpty);
    }
}
