using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayRegionSignatureTests
{
    [TestMethod]
    public void SamePhysicalShapeAndPositionSkips()
    {
        var policy = new OverlayRegionUpdatePolicy();
        var first = OverlayRegionSignature.Create(120, 24, 340, 64, 32, 32, 1.25);

        Assert.IsTrue(policy.ShouldApply(first));
        Assert.IsFalse(policy.ShouldApply(first));
        Assert.AreEqual(1, policy.ApplyAttempts);
        Assert.AreEqual(1, policy.SkippedUpdates);
    }

    [TestMethod]
    public void ChangingLeftOrTopIsARealRegionChange()
    {
        var policy = new OverlayRegionUpdatePolicy();
        var first = OverlayRegionSignature.Create(120, 24, 340, 64, 32, 32, 1.25);
        var movedLeft = first with { LeftPixels = 121 };
        var movedTop = first with { TopPixels = 25 };

        Assert.IsTrue(policy.ShouldApply(first));
        Assert.IsTrue(policy.ShouldApply(movedLeft));
        Assert.IsTrue(policy.ShouldApply(movedTop));
        Assert.AreEqual(3, policy.ApplyAttempts);
    }

    [TestMethod]
    public void DpiAndEmptyTransitionsRemainDistinct()
    {
        var policy = new OverlayRegionUpdatePolicy();
        var at100 = OverlayRegionSignature.Create(0, 0, 340, 64, 32, 32, 1);
        var at125 = OverlayRegionSignature.Create(0, 0, 340, 64, 32, 32, 1.25);

        Assert.IsTrue(policy.ShouldApply(at100));
        Assert.IsTrue(policy.ShouldApply(at125));
        Assert.IsTrue(policy.ShouldApply(OverlayRegionSignature.Empty));
        Assert.IsTrue(policy.ShouldApply(at100));
        Assert.AreEqual(4, policy.ApplyAttempts);
    }

    [TestMethod]
    public void NegativeMonitorCoordinatesRemainRepresentable()
    {
        var signature = OverlayRegionSignature.Create(-1920, -120, 340, 64, 32, 32, 1.5);

        Assert.AreEqual(-1920, signature.LeftPixels);
        Assert.AreEqual(-120, signature.TopPixels);
        Assert.IsFalse(signature.IsEmpty);
    }
}
