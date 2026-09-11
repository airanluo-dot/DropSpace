using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using DropSpace.Core.Widgets;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class Preview22NativeIslandTests
{
    [TestMethod]
    public void RouterSelectsLowerPriorityActivityAndKeepsStableSourceUpdates()
    {
        using var router = new IslandActivityRouter();
        var mediaId = router.Publish(Activity(IslandActivityKind.Media, IslandActivityPriority.Media, "media"));
        var notificationId = router.Publish(Activity(IslandActivityKind.Notification, IslandActivityPriority.Notification, "toast"));

        Assert.AreEqual(notificationId, router.Snapshot.Current!.Id);

        router.Publish(Activity(IslandActivityKind.Media, IslandActivityPriority.Media, "media", mediaId));
        Assert.AreEqual(notificationId, router.Snapshot.Current!.Id);
        Assert.AreEqual(1, router.RemoveSource("toast"));
        Assert.AreEqual(mediaId, router.Snapshot.Current!.Id);
    }

    [TestMethod]
    public void NativeActivityWakesAnEmptyIslandAndAllowsExpandedMediaView()
    {
        var stateMachine = new OverlayStateMachine();
        stateMachine.Restore(0);

        stateMachine.SetNativeActivityVisible(true);
        Assert.AreEqual(OverlayState.Compact, stateMachine.Snapshot.State);

        stateMachine.Expand();
        Assert.AreEqual(OverlayState.Expanded, stateMachine.Snapshot.State);
    }

    [TestMethod]
    public void LyricsAreOptInByDefault()
    {
        Assert.IsFalse(new LyricsSettings().Enabled);
        Assert.AreEqual(LyricsMode.Online, new LyricsSettings().Mode);
    }

    [TestMethod]
    public void LyricsParserPreservesMultipleTimestampsAndSecondaryText()
    {
        var lines = LyricsParser.Parse("[00:01.20][00:02.50]primary\n[00:04.00]next");

        Assert.AreEqual(3, lines.Count);
        Assert.AreEqual("primary", lines[0].PrimaryText);
        Assert.AreEqual(TimeSpan.FromSeconds(1.2), lines[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), lines[1].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(4), lines[2].Start);
    }

    [TestMethod]
    public void WidgetLayoutPolicyNormalizesDuplicatesAndBoundsToSixByThree()
    {
        var layout = WidgetLayoutPolicy.Normalize(new WidgetLayout(
            [
                new(NativeWidgetId.Clock, -4, -2, 20, 20),
                new(NativeWidgetId.Clock, 0, 0, 1, 1),
                new(NativeWidgetId.Calendar, 0, 0, 2, 2),
            ],
            new(NativeWidgetId.Clock, NativeWidgetId.Clock, NativeWidgetId.ResourceUsage)));

        Assert.AreEqual(1, layout.Expanded.Count);
        var placement = layout.Expanded[0];
        Assert.IsTrue(placement.Column >= 0 && placement.Row >= 0);
        Assert.IsTrue(placement.Column + placement.ColumnSpan <= WidgetLayoutPolicy.Columns);
        Assert.IsTrue(placement.Row + placement.RowSpan <= WidgetLayoutPolicy.Rows);
        Assert.AreEqual(NativeWidgetId.Clock, layout.Compact.Left);
        Assert.IsNull(layout.Compact.Center);
        Assert.AreEqual(NativeWidgetId.ResourceUsage, layout.Compact.Right);
    }

    private static IslandActivity Activity(IslandActivityKind kind, IslandActivityPriority priority, string source, Guid? id = null) =>
        new(
            id ?? Guid.Empty,
            kind,
            priority,
            IslandActivityPresentation.Both,
            source,
            source,
            source,
            source,
            source,
            new(DateTimeOffset.UtcNow));
}
