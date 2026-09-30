using DropSpace.Core.Overlay;
namespace DropSpace.Core.Tests;
[TestClass]
public sealed class OverlayContentPoseTests
{
    [TestMethod]
    public void EachLayerHasItsOwnPoseAndReturnsToRest()
    {
        Assert.AreEqual(new OverlayContentPose(0, 1), OverlayContentPose.FromProgress(1, false));
        Assert.AreEqual(2d, OverlayContentPose.FromProgress(0.5, false).OffsetY);
        Assert.IsTrue(OverlayContentPose.FromProgress(0.5, false).Scale < 1);
    }
    [TestMethod]
    public void ReversalDoesNotResetContentPose()
    {
        var a = new OverlayMotionValues(120, 40, 8, 20, 20, 1, 1, 0, 0, 1);
        var b = a with { Width = 500, Height = 300, CompactContent = 0, ExpandedContent = 1 };
        var controller = new OverlayMotionController(a);
        controller.SetTarget(b, false);
        controller.Step(TimeSpan.FromMilliseconds(64));
        var before = OverlayContentPose.FromProgress(controller.Current.ExpandedContent, false);
        controller.SetTarget(a, false);
        Assert.AreEqual(before, OverlayContentPose.FromProgress(controller.Current.ExpandedContent, false));
    }
    [TestMethod]
    public void ReducedMotionRemovesTranslationAndScale()
    {
        foreach (var progress in new[] { 0d, 0.3, 1 })
            Assert.AreEqual(new OverlayContentPose(0, 1), OverlayContentPose.FromProgress(progress, true));
    }
}
