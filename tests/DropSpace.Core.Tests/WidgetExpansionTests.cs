using DropSpace.Core.Widgets;
namespace DropSpace.Core.Tests;
[TestClass]
public sealed class WidgetExpansionTests
{
    [TestMethod]
    public void CatalogHasSixteenWidgetsWithoutChangingExistingIds()
    {
        Assert.AreEqual(16, Enum.GetValues<NativeWidgetId>().Length);
        Assert.AreEqual(0, (int)Enum.Parse<NativeWidgetId>("Clock"));
        Assert.AreEqual(7, (int)Enum.Parse<NativeWidgetId>("ClipboardPause"));
        foreach (var id in Enum.GetValues<NativeWidgetId>())
        {
            var empty = new WidgetLayout([], new(null, null, null));
            Assert.IsTrue(WidgetLayoutPolicy.TryAdd(empty, id, 0, 0, out var added));
            Assert.AreEqual(id, added.Expanded.Single().Id);
            Assert.AreEqual(added, WidgetLayoutPolicy.Normalize(added));
        }
        Assert.AreEqual(WidgetLayout.Default, WidgetLayoutPolicy.Normalize(WidgetLayout.Default));
    }
    [TestMethod]
    public void TimerSupportsStartPauseAndReset()
    {
        var timer = new WidgetCountdown(TimeSpan.FromMinutes(25));
        Assert.AreEqual(TimeSpan.FromMinutes(25), timer.Remaining);
        timer.Toggle(); Assert.IsTrue(timer.IsRunning);
        timer.Toggle(); Assert.IsFalse(timer.IsRunning);
        var paused = timer.Remaining;
        Assert.AreEqual(paused, timer.Remaining);
        timer.Reset(); Assert.AreEqual(TimeSpan.FromMinutes(25), timer.Remaining);
    }
    [TestMethod]
    public void ZeroDurationNeverReturnsNegativeTime()
    {
        var timer = new WidgetCountdown(TimeSpan.Zero);
        timer.Toggle();
        Assert.AreEqual(TimeSpan.Zero, timer.Remaining);
        Assert.IsFalse(timer.IsRunning);
    }
}
