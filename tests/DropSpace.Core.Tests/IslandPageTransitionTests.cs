using DropSpace.Core.Island;
namespace DropSpace.Core.Tests;
[TestClass]
public sealed class IslandPageTransitionTests
{
    [TestMethod]
    public void RapidPageSwitchPreservesCurrentOpacityAndSettles()
    {
        var motion = new IslandPageTransition();
        motion.Select(IslandPage.Music, false);
        motion.Step(TimeSpan.FromMilliseconds(60));
        var before = Enum.GetValues<IslandPage>().Select(motion.Progress).ToArray();
        motion.Select(IslandPage.Widgets, false);
        CollectionAssert.AreEqual(before, Enum.GetValues<IslandPage>().Select(motion.Progress).ToArray());
        for (var i = 0; i < 60; i++)
        {
            motion.Step(TimeSpan.FromMilliseconds(16));
            Assert.AreEqual(1d, Enum.GetValues<IslandPage>().Sum(motion.Progress), 0.00001);
        }
        Assert.IsFalse(motion.IsAnimating);
        Assert.AreEqual(1d, motion.Progress(IslandPage.Widgets));
        Assert.AreEqual(0d, motion.Progress(IslandPage.Music));
    }
    [TestMethod]
    public void ReducedMotionCompletesWithoutWaitingForFrames()
    {
        var motion = new IslandPageTransition();
        motion.Select(IslandPage.Clipboard, true);
        Assert.IsFalse(motion.IsAnimating);
        Assert.AreEqual(1d, motion.Progress(IslandPage.Clipboard));
    }
}
