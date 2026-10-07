using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class IslandContentPriorityTests
{
    [TestMethod]
    public void DefaultMusicAppliesToCompactExpandedAndPlaybackFallback()
    {
        var island = new IslandExperienceCoordinator();
        island.UpdateFiles(new(OverlayState.Compact, 2, false, 1));
        island.UpdateMedia(true, true, true, 3000, "player/song");
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        island.Open();
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
        island.Collapse();
        island.UpdateMedia(true, false, true, 3000, "player/song");
        Assert.AreEqual(IslandContentKind.Files, island.Current.CompactContent);
        island.Open();
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
        island.UpdateMedia(true, true, true, 3000, "player/song");
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
        Assert.AreEqual(OverlayState.Expanded, island.Current.State);
    }

    [TestMethod]
    public void RestoredTemporarySpacePriorityUsesAvailableContentInBothPresentations()
    {
        // A fresh coordinator receives the saved setting before either projection.
        var island = new IslandExperienceCoordinator();
        island.UpdateContentPriority(IslandContentPriority.TemporarySpace);
        island.UpdateMedia(true, true, true, 3000, "player/song");
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        island.UpdateFiles(new(OverlayState.Compact, 1, false, 1));
        Assert.AreEqual(IslandContentKind.Files, island.Current.CompactContent);
        island.Open();
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
        island.UpdateFiles(new(OverlayState.Expanded, 0, false, 2));
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
        island.Collapse();
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        island.UpdateFiles(new(OverlayState.Compact, 1, false, 3));
        island.UpdateMedia(false, false, true, 3000);
        island.Open();
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
        Assert.AreEqual(IslandContentKind.Files, island.Current.CompactContent);
    }

    [TestMethod]
    public void ManualMusicChoiceSurvivesRefreshPlayerChangesPauseAndReopenUntilUnavailable()
    {
        var island = new IslandExperienceCoordinator();
        island.UpdateContentPriority(IslandContentPriority.TemporarySpace);
        island.UpdateFiles(new(OverlayState.Compact, 2, false, 1));
        island.UpdateMedia(true, true, true, 3000, "player/song");
        island.Open();
        island.SelectPage(IslandPage.Music);
        island.UpdateMedia(true, true, true, 3000, "player/song");
        island.Reconcile(); // Lyrics, timer and rendering refreshes cannot select pages.
        island.UpdateMedia(true, true, true, 3000, "other-player/other-song");
        island.UpdateMedia(true, false, true, 3000, "other-player/other-song");
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
        island.Collapse();
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        island.Open();
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
        island.UpdateMedia(false, false, true, 3000);
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
        Assert.AreEqual(IslandContentKind.Files, island.Current.CompactContent);
        island.UpdateMedia(true, true, true, 3000, "restarted-player/song");
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
    }

    [TestMethod]
    public void ManualFilesChoiceSurvivesAddRefreshAndCollapseButClearingRestoresMusic()
    {
        var island = new IslandExperienceCoordinator();
        island.UpdateMedia(true, true, true, 3000, "song");
        island.UpdateFiles(new(OverlayState.Compact, 1, false, 1));
        island.Open();
        island.SelectPage(IslandPage.Files);
        island.UpdateMedia(true, true, true, 3000, "song");
        island.UpdateFiles(new(OverlayState.Expanded, 2, false, 2));
        island.Collapse();
        Assert.AreEqual(IslandContentKind.Files, island.Current.CompactContent);
        island.Open();
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
        island.UpdateFiles(new(OverlayState.Expanded, 0, false, 3));
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        island.UpdateFiles(new(OverlayState.Expanded, 1, false, 4));
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
    }

    [TestMethod]
    public void DragPreviewAndDropRetainTheChosenPageWithoutLosingFileDropOwnership()
    {
        var island = new IslandExperienceCoordinator();
        island.UpdateContentPriority(IslandContentPriority.TemporarySpace);
        island.UpdateMedia(true, true, true, 3000, "song");
        island.UpdateFiles(new(OverlayState.Compact, 1, false, 1));
        island.Open(IslandPage.Music);
        island.UpdateFiles(new(OverlayState.Expanded, 1, true, 2));
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
        Assert.AreEqual(IslandContentKind.Files, island.Current.CompactContent);
        island.UpdateFiles(new(OverlayState.Expanded, 2, false, 3));
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        island.Collapse();
        island.UpdateFiles(new(OverlayState.DragReady, 2, false, 4));
        Assert.AreEqual(OverlayState.DragReady, island.Current.State);
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
        island.UpdateFiles(new(OverlayState.Compact, 3, false, 5));
        Assert.AreEqual(IslandContentKind.Music, island.Current.CompactContent);
        island.Open();
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
    }

    [TestMethod]
    public void PriorityChangeImmediatelyResetsChoiceWhileRepeatedSettingsAndActivitiesDoNot()
    {
        var clock = new Clock();
        var island = new IslandExperienceCoordinator(clock);
        island.UpdateFiles(new(OverlayState.Compact, 1, false, 1));
        island.UpdateMedia(true, true, true, 3000, "song");
        island.Open(IslandPage.Widgets);
        island.UpdateContentPriority(IslandContentPriority.Music);
        Assert.AreEqual(IslandPage.Widgets, island.Current.Page);
        island.Notify();
        Assert.AreEqual(IslandContentKind.Notification, island.Current.CompactContent);
        clock.Now += TimeSpan.FromMinutes(1);
        island.Reconcile();
        Assert.AreEqual(IslandPage.Widgets, island.Current.Page);
        island.UpdateContentPriority(IslandContentPriority.TemporarySpace);
        Assert.AreEqual(IslandPage.Files, island.Current.Page);
        Assert.AreEqual(IslandContentKind.Files, island.Current.CompactContent);
        island.SelectPage(IslandPage.Music);
        island.UpdateContentPriority(IslandContentPriority.TemporarySpace);
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
        island.UpdateContentPriority(IslandContentPriority.Music);
        Assert.AreEqual(IslandPage.Music, island.Current.Page);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
