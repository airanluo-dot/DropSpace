using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class Beta15IslandLifecycleTests
{
    [TestMethod]
    public void PauseRetainsMusicUntilConfiguredDelayAndAnimationCompletion()
    {
        var clock = new Clock();
        var island = new IslandExperienceCoordinator(clock);
        island.UpdateSettings(new() { Resident = false, HideDelayMilliseconds = 5000 }, true);
        island.UpdateMedia(true, true, true, 5000, "song");
        island.UpdateMedia(true, false, true, 5000, "song");
        var deadline = island.Current.NextDeadline!.Value;
        var generation = island.HideGeneration;
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        Assert.IsTrue(island.Current.MediaPresent);
        Assert.IsTrue(island.Current.PendingHide);
        clock.Now += TimeSpan.FromSeconds(4);
        island.UpdateMedia(true, false, true, 5000, "song");
        Assert.AreEqual(deadline, island.Current.NextDeadline);
        Assert.AreEqual(OverlayState.Compact, island.Current.State);
        clock.Now += TimeSpan.FromSeconds(1);
        island.OnDeadline(generation, deadline);
        Assert.AreEqual(OverlayState.Dismissing, island.Current.State);
        Assert.AreEqual(IslandPresentationVariant.Music, island.Current.Variant);
        Assert.IsTrue(island.Current.MediaPresent);
        Assert.IsNull(island.Current.NextDeadline);
        Assert.IsTrue(island.CompleteDismissal(generation));
        Assert.AreEqual(OverlayState.Hidden, island.Current.State);
        Assert.IsFalse(island.Current.MediaPresent);
        Assert.IsFalse(island.CompleteDismissal(generation), "A finished hide cannot complete twice.");
    }

    [TestMethod]
    public void PlaybackFilesAndManualExpansionRejectObsoleteHideCompletion()
    {
        foreach (var reason in new[] { "playback", "files", "expand" })
        {
            var clock = new Clock();
            var island = new IslandExperienceCoordinator(clock);
            island.UpdateMedia(true, true, true, 2000, "song");
            island.UpdateMedia(true, false, true, 2000, "song");
            var generation = island.HideGeneration;
            clock.Now += TimeSpan.FromSeconds(2);
            island.Reconcile();
            Assert.AreEqual(OverlayState.Dismissing, island.Current.State);
            if (reason == "playback") island.UpdateMedia(true, true, true, 2000, "song");
            else if (reason == "files") island.UpdateFiles(new(OverlayState.Compact, 1, false, 1));
            else island.Open(IslandPage.Files);
            Assert.IsFalse(island.CompleteDismissal(generation), reason);
            Assert.AreEqual(reason == "expand" ? OverlayState.Expanded : OverlayState.Compact, island.Current.State);
            Assert.IsNull(island.Current.NextDeadline);
        }
    }

    [TestMethod]
    public void ResidentPausedSessionRetainsMusicWhileUnavailableSessionUsesIdleRules()
    {
        var clock = new Clock();
        var island = new IslandExperienceCoordinator(clock);
        island.UpdateSettings(new() { Resident = true, HideDelayMilliseconds = 3000 }, true);
        island.UpdateMedia(true, true, true, 3000, "song");
        island.UpdateMedia(true, false, true, 3000, "song");
        clock.Now += TimeSpan.FromHours(1);
        island.Reconcile();
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        Assert.IsNull(island.Current.NextDeadline);
        island.UpdateMedia(false, false, true, 3000);
        Assert.AreEqual(OverlayState.Compact, island.Current.State);
        Assert.AreEqual(IslandPresentationVariant.Idle, island.Current.Variant);
        Assert.IsFalse(island.Current.MediaPresent);
    }

    [TestMethod]
    public void EmptyIdleExpiryAnimatesOnceAndOpeningCancelsOldDeadline()
    {
        var clock = new Clock();
        var island = new IslandExperienceCoordinator(clock);
        island.UpdateSettings(new() { Resident = true, HideDelayMilliseconds = 4000 }, true);
        island.UpdateSettings(new() { Resident = false, HideDelayMilliseconds = 4000 }, true);
        var deadline = island.Current.NextDeadline!.Value;
        var generation = island.HideGeneration;
        clock.Now += TimeSpan.FromSeconds(4);
        island.OnDeadline(generation, deadline);
        Assert.AreEqual(OverlayState.Dismissing, island.Current.State);
        Assert.IsNull(island.Current.NextDeadline);
        island.Reconcile();
        Assert.AreEqual(generation, island.HideGeneration);
        island.Open(IslandPage.Files);
        island.OnDeadline(generation, deadline);
        Assert.IsFalse(island.CompleteDismissal(generation));
        Assert.AreEqual(OverlayState.Expanded, island.Current.State);
        Assert.IsNull(island.Current.NextDeadline);
    }

    [TestMethod]
    public void ManualDismissStartsAnimationWithoutWaitingAnotherHideDelay()
    {
        var island = new IslandExperienceCoordinator(new Clock());
        island.UpdateMedia(true, true, true, 10000, "song");
        island.DismissNow();
        Assert.AreEqual(OverlayState.Dismissing, island.Current.State);
        Assert.IsNull(island.Current.NextDeadline);
        Assert.IsTrue(island.CompleteDismissal(island.HideGeneration));
        island.UpdateMedia(true, true, true, 10000, "song");
        Assert.AreEqual(OverlayState.Hidden, island.Current.State);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
