using DropSpace.App.Views.Island;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaSeekInteractionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SlowDragSuppressesEveryPlaybackFrameAndCommitsOnlyOnRelease()
    {
        var interaction = new MediaSeekInteraction(TimeSpan.FromSeconds(2));
        interaction.Begin(10);

        for (var step = 1; step <= 500; step++)
        {
            interaction.Preview(10 + step / 5d);
            Assert.IsFalse(interaction.ShouldApplyPlayback(10 + step / 30d, Now.AddMilliseconds(step * 10), canSeek: true));
        }

        Assert.AreEqual(110, interaction.PreviewSeconds, 0.001);
        Assert.AreEqual(110, interaction.Complete(canceled: false, Now));
        Assert.IsNull(interaction.Complete(canceled: false, Now));
    }

    [TestMethod]
    public void ReleasedTargetDoesNotSnapBackBeforePlayerAcknowledgesSeek()
    {
        var interaction = new MediaSeekInteraction(TimeSpan.FromSeconds(2));
        interaction.Begin(15);
        interaction.Preview(90);
        Assert.AreEqual(90, interaction.Complete(canceled: false, Now));

        Assert.IsTrue(interaction.IsPendingTarget(90));
        Assert.IsFalse(interaction.ShouldApplyPlayback(16, Now.AddMilliseconds(500), canSeek: true));
        Assert.IsTrue(interaction.ShouldApplyPlayback(89.2, Now.AddSeconds(1), canSeek: true));
        Assert.IsFalse(interaction.IsPendingTarget(90));
    }

    [TestMethod]
    public void PendingTargetExpiresAndCanceledDragNeverCommits()
    {
        var interaction = new MediaSeekInteraction(TimeSpan.FromSeconds(2));
        interaction.Begin(20);
        interaction.Preview(70);
        Assert.IsNull(interaction.Complete(canceled: true, Now));
        Assert.IsTrue(interaction.ShouldApplyPlayback(21, Now, canSeek: true));

        interaction.Commit(80, Now);
        Assert.IsFalse(interaction.ShouldApplyPlayback(22, Now.AddMilliseconds(1999), canSeek: true));
        Assert.IsTrue(interaction.ShouldApplyPlayback(22, Now.AddSeconds(2), canSeek: true));
    }

    [TestMethod]
    public void TrackChangeResetImmediatelyRestoresPlaybackOwnership()
    {
        var interaction = new MediaSeekInteraction(TimeSpan.FromSeconds(2));
        interaction.Begin(5);
        interaction.Preview(55);
        interaction.Reset();

        Assert.IsFalse(interaction.IsDragging);
        Assert.IsTrue(interaction.ShouldApplyPlayback(3, Now, canSeek: true));
        Assert.IsFalse(interaction.IsPendingTarget(55));
    }
}
