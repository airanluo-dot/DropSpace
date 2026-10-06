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

    [TestMethod]
    public void LastFileDismissalFinishesWithoutResettingIslandHideWait()
    {
        foreach (var delay in new[] { 0, 3000 })
        {
            var (clock, island, files) = FileIsland(delay);
            files.SetTemporaryItemCount(0);
            var fileRevision = files.Snapshot.Revision;
            var generation = island.HideGeneration;
            var deadline = island.Current.NextDeadline;
            Assert.AreEqual(OverlayState.Dismissing, files.Snapshot.State);
            Assert.AreEqual(delay == 0 ? OverlayState.Dismissing : OverlayState.Compact, island.Current.State);
            Assert.IsTrue(island.CanCompleteFileDismissal(fileRevision));
            files.CompleteDismissal();
            Assert.AreEqual(OverlayState.Hidden, files.Snapshot.State);
            Assert.AreEqual(generation, island.HideGeneration);
            Assert.AreEqual(deadline, island.Current.NextDeadline, "File cleanup cannot restart the hide clock.");
            Assert.IsFalse(island.CanCompleteFileDismissal(fileRevision));
            if (deadline is { } expires)
            {
                clock.Now = expires;
                island.OnDeadline(generation, expires);
            }
            Assert.AreEqual(OverlayState.Dismissing, island.Current.State, "File cleanup cannot skip the island animation.");
            Assert.IsTrue(island.CompleteDismissal(generation));
            Assert.AreEqual(OverlayState.Hidden, island.Current.State);
        }
    }

    [TestMethod]
    public void MusicAndResidencePermitFileCleanupWhileIslandStaysVisible()
    {
        foreach (var music in new[] { false, true })
        {
            var (clock, island, files) = FileIsland(3000);
            if (music) island.UpdateMedia(true, true, true, 3000, "song");
            else island.UpdateSettings(new() { Resident = true, HideDelayMilliseconds = 3000 }, true);
            files.SetTemporaryItemCount(0);
            Assert.AreEqual(OverlayState.Compact, island.Current.State);
            Assert.IsTrue(island.CanCompleteFileDismissal(files.Snapshot.Revision));
            files.CompleteDismissal();
            clock.Now += TimeSpan.FromHours(1);
            island.Reconcile();
            Assert.AreEqual(OverlayState.Hidden, files.Snapshot.State);
            Assert.AreEqual(OverlayState.Compact, island.Current.State);
            Assert.AreEqual(music ? IslandPresentationVariant.Music : IslandPresentationVariant.Idle, island.Current.Variant);
            Assert.IsNull(island.Current.NextDeadline);
        }
    }

    [TestMethod]
    public void EmptyPanelCollapseAndDirectHideCanSettleFileOwnership()
    {
        var (_, island, files) = FileIsland(0);
        files.SetTemporaryItemCount(0);
        island.Open(IslandPage.Files);
        files.OpenQuickPanel();
        island.Collapse();
        files.Collapse();
        var revision = files.Snapshot.Revision;
        Assert.IsTrue(island.CanCompleteFileDismissal(revision));
        Assert.AreEqual(OverlayState.Dismissing, island.Current.State);
        // A reduced animation or necessary native safety hide may already have
        // completed the presentation before the file state receives its callback.
        Assert.IsTrue(island.CompleteDismissal(island.HideGeneration));
        Assert.AreEqual(OverlayState.Hidden, island.Current.State);
        Assert.IsTrue(island.CanCompleteFileDismissal(revision));
        files.CompleteDismissal();
        Assert.AreEqual(OverlayState.Hidden, files.Snapshot.State);
        Assert.AreEqual(OverlayState.Hidden, island.Current.State);
        Assert.IsFalse(island.CanCompleteFileDismissal(revision));
    }

    [TestMethod]
    public void NewExpansionFileOrPlaybackRejectsOldHideWithoutClosingNewContent()
    {
        foreach (var reason in new[] { "expand", "file", "playback" })
        {
            var (_, island, files) = FileIsland(0);
            files.SetTemporaryItemCount(0);
            var fileRevision = files.Snapshot.Revision;
            var generation = island.HideGeneration;
            if (reason == "expand")
            {
                island.Open(IslandPage.Files);
                Assert.IsFalse(island.CanCompleteFileDismissal(fileRevision), "Manual opening protects even an awaiting file refresh.");
                files.Expand();
            }
            else if (reason == "file") files.SetTemporaryItemCount(1);
            else island.UpdateMedia(true, true, true, 0, "song");
            Assert.IsFalse(island.CompleteDismissal(generation));
            if (reason == "playback")
            {
                Assert.IsTrue(island.CanCompleteFileDismissal(fileRevision));
                files.CompleteDismissal();
                Assert.AreEqual(OverlayState.Hidden, files.Snapshot.State);
                Assert.AreEqual(IslandPresentationVariant.Music, island.Current.Variant);
            }
            else Assert.IsFalse(island.CanCompleteFileDismissal(fileRevision));
            Assert.AreEqual(reason == "expand" ? OverlayState.Expanded : OverlayState.Compact, island.Current.State);
            Assert.IsNull(island.Current.NextDeadline);
        }
    }

    private static (Clock Clock, IslandExperienceCoordinator Island, OverlayStateMachine Files) FileIsland(int delay)
    {
        var clock = new Clock();
        var island = new IslandExperienceCoordinator(clock);
        island.UpdateSettings(new() { Resident = false, HideDelayMilliseconds = delay }, true);
        var files = new OverlayStateMachine();
        files.Changed += (_, snapshot) => island.UpdateFiles(snapshot);
        files.Restore(1);
        return (clock, island, files);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
