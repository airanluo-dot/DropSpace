using DropSpace.App.Services;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MonitorTopologyRegressionTests
{
    private static MonitorDescriptor Screen(string id, int x = 0) => new(id, (nint)1, x, 0, 1920, 1080, 96, x == 0, 0, 0, 1920, 1040);

    [TestMethod]
    public void RepeatedAndReorderedDisplaySnapshotsKeepExistingSurfaces()
    {
        var first = Screen("one"); var second = Screen("two", 1920);
        Assert.IsTrue(OverlayWindowService.SameMonitorTopology([first, second], [second with { }, first with { }]));
    }

    [TestMethod]
    public void EveryPlacementRelevantChangeRequiresRefresh()
    {
        var original = Screen("one");
        foreach (var changed in new[]
        {
            original with { Dpi = 144 }, original with { WorkHeight = 1000 },
            original with { IsPrimary = false }, original with { Left = -1920 },
            original with { Width = 2560 }, original with { Height = 1440 },
            original with { Handle = (nint)2 }, original with { Id = "replacement" }
        }) Assert.IsFalse(OverlayWindowService.SameMonitorTopology([original], [changed]));
        Assert.IsFalse(OverlayWindowService.SameMonitorTopology([original], []));
        Assert.IsFalse(OverlayWindowService.SameMonitorTopology([original], [original, Screen("two")]));
    }
}
