using DropSpace.Core.Island;
using DropSpace.Core.Overlay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class IslandPresencePolicyTests
{
    [TestMethod]
    public void PauseDeadlineHidesWithoutAllowingWidgetsToKeepPresence()
    {
        var time = new ManualTime();
        var coordinator = new IslandExperienceCoordinator(time);
        coordinator.SelectPage(IslandPage.Widgets);
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
        coordinator.UpdateMedia(true, true, 3000);
        coordinator.UpdateMedia(false, true, 3000);
        time.Now += TimeSpan.FromMilliseconds(2999); coordinator.Reconcile();
        Assert.IsTrue(coordinator.Current.MediaPresent);
        time.Now += TimeSpan.FromMilliseconds(1); coordinator.Reconcile();
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
    }

    [TestMethod]
    public void FilesResumeAfterPauseAndTransientRestoresChosenPage()
    {
        var time = new ManualTime();
        var coordinator = new IslandExperienceCoordinator(time);
        coordinator.UpdateFiles(new(OverlayState.Compact, 2, false, 1));
        coordinator.UpdateMedia(true, true, 3000);
        coordinator.Open(IslandPage.Widgets);
        coordinator.Notify();
        Assert.AreEqual(IslandContentKind.Notification, coordinator.Current.CompactContent);
        coordinator.UpdateMedia(false, true, 3000);
        time.Now += TimeSpan.FromSeconds(5); coordinator.Reconcile();
        Assert.AreEqual(OverlayState.Expanded, coordinator.Current.State);
        Assert.AreEqual(IslandPage.Widgets, coordinator.Current.Page);
        coordinator.Collapse();
        Assert.AreEqual(IslandContentKind.Files, coordinator.Current.CompactContent);
    }

    [TestMethod]
    public void RepeatedPausedUpdatesDoNotExtendGraceAndNewPlaybackCancelsDeadline()
    {
        var time = new ManualTime();
        var coordinator = new IslandExperienceCoordinator(time);
        coordinator.UpdateMedia(true, true, 3000);
        coordinator.UpdateMedia(false, true, 3000);
        var deadline = coordinator.Current.NextDeadline;
        time.Now += TimeSpan.FromSeconds(2);
        coordinator.UpdateMedia(false, true, 3000);
        Assert.AreEqual(deadline, coordinator.Current.NextDeadline);
        coordinator.UpdateMedia(true, true, 3000);
        Assert.IsNull(coordinator.Current.NextDeadline);
        coordinator.UpdateMedia(false, false, 3000);
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
    }

    [TestMethod]
    public void DragTemporarilyShowsFilesAndPlaybackDoesNotStealChosenPage()
    {
        var time = new ManualTime();
        var coordinator = new IslandExperienceCoordinator(time);
        coordinator.Open(IslandPage.Widgets);
        coordinator.UpdateMedia(true, true, 3000);
        Assert.AreEqual(IslandPage.Widgets, coordinator.Current.Page);
        coordinator.UpdateFiles(new(OverlayState.Expanded, 0, true, 1));
        Assert.AreEqual(IslandPage.Files, coordinator.Current.Page);
        coordinator.UpdateFiles(new(OverlayState.Hidden, 0, false, 2));
        Assert.AreEqual(IslandPage.Widgets, coordinator.Current.Page);
        coordinator.UpdateMedia(false, true, 3000);
        time.Now += TimeSpan.FromSeconds(4); coordinator.Reconcile();
        Assert.AreEqual(IslandPage.Widgets, coordinator.Current.Page);
        Assert.AreEqual(OverlayState.Expanded, coordinator.Current.State);
    }

    [TestMethod]
    public void ManualQuickPanelDefaultsToFilesDuringPausedGrace()
    {
        var coordinator = new IslandExperienceCoordinator(new ManualTime());
        coordinator.UpdateMedia(true, true, 3000);
        coordinator.Open();
        Assert.AreEqual(IslandPage.Music, coordinator.Current.Page);
        coordinator.Collapse(); coordinator.UpdateMedia(false, true, 3000);
        coordinator.Open();
        Assert.AreEqual(IslandPage.Files, coordinator.Current.Page);
    }

    [TestMethod]
    public void AutoHideOffRetainsOnlyMediaAndReenablingStartsOneGracePeriod()
    {
        var time = new ManualTime();
        var coordinator = new IslandExperienceCoordinator(time);
        coordinator.UpdateMedia(false, true, 3000, autoHide: false);
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
        coordinator.UpdateMedia(true, true, 3000, autoHide: false);
        coordinator.UpdateMedia(false, true, 3000, autoHide: false);
        time.Now += TimeSpan.FromHours(1); coordinator.Reconcile();
        Assert.IsTrue(coordinator.Current.MediaPresent);
        Assert.IsNull(coordinator.Current.NextDeadline);
        coordinator.UpdateMedia(false, true, 3000);
        var deadline = coordinator.Current.NextDeadline;
        time.Now += TimeSpan.FromSeconds(2); coordinator.UpdateMedia(false, true, 3000);
        Assert.AreEqual(deadline, coordinator.Current.NextDeadline);
        time.Now += TimeSpan.FromSeconds(1); coordinator.Reconcile();
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
        coordinator.UpdateMedia(true, true, 3000, autoHide: false);
        coordinator.UpdateMedia(false, false, 3000, autoHide: false);
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
