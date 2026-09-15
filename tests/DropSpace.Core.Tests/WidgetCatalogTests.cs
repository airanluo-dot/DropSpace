using DropSpace.Core.Widgets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class WidgetCatalogTests
{
    [TestMethod]
    public void AddingAtOccupiedCellPreservesEveryExistingPlacement()
    {
        var original = new WidgetLayout(WidgetLayout.Default.Expanded.Where(item => item.Id != NativeWidgetId.Calendar).ToArray(), WidgetLayout.Default.Compact);
        Assert.IsTrue(WidgetLayoutPolicy.TryAdd(original, NativeWidgetId.Calendar, 0, 0, out var added));
        CollectionAssert.AreEqual(original.Expanded.ToArray(), added.Expanded.Take(original.Expanded.Count).ToArray());
        Assert.AreEqual(new WidgetPlacement(NativeWidgetId.Calendar, 2, 0, 2, 2), added.Expanded.Last());
        Assert.AreEqual(added, WidgetLayoutPolicy.Normalize(added));
    }

    [TestMethod]
    public void AddingWithoutContiguousRoomLeavesLayoutUntouched()
    {
        var original = new WidgetLayout([new(NativeWidgetId.Clock, 0, 0, 8, 4)], WidgetLayout.Default.Compact);
        Assert.IsFalse(WidgetLayoutPolicy.TryAdd(original, NativeWidgetId.Calendar, 0, 0, out var result));
        Assert.AreSame(original, result);
        Assert.IsFalse(WidgetLayoutPolicy.TryAdd(WidgetLayout.Default, NativeWidgetId.Clock, 0, 0, out result));
        Assert.AreSame(WidgetLayout.Default, result);
    }

    [TestMethod]
    public void EightByFourGridAcceptsSupportedCompactSizesAtLastCell()
    {
        var layout = WidgetLayoutPolicy.Normalize(new([new(NativeWidgetId.Clock, 7, 3, 1, 1)], new(null, null, null)));
        Assert.AreEqual(new WidgetPlacement(NativeWidgetId.Clock, 7, 3, 1, 1), layout.Expanded.Single());
    }
    [TestMethod]
    public void ResizingAndCollisionResolutionPreserveAllWidgets()
    {
        var desired = new WidgetPlacement(NativeWidgetId.Clock, 2, 0, 2, 2);
        var layout = WidgetLayoutPolicy.Normalize(new(new[] { desired }.Concat(WidgetLayout.Default.Expanded.Where(item => item.Id != NativeWidgetId.Clock)).ToArray(), WidgetLayout.Default.Compact));
        Assert.AreEqual(WidgetLayout.Default.Expanded.Count, layout.Expanded.Count);
        var occupied = new HashSet<(int,int)>();
        foreach (var item in layout.Expanded)
        for (var row = item.Row; row < item.Row + item.RowSpan; row++)
        for (var column = item.Column; column < item.Column + item.ColumnSpan; column++)
            Assert.IsTrue(occupied.Add((column,row)), "Widgets must not overlap.");
        Assert.AreEqual(desired, layout.Expanded[0]);
    }
    [TestMethod]
    public void EveryWidgetHasABoundedSizeAndOversizedInputNormalizes()
    {
        foreach (var id in Enum.GetValues<NativeWidgetId>())
        {
            var size = WidgetCatalog.NormalizeSize(id, int.MaxValue, int.MinValue);
            Assert.IsTrue(size.Columns is >= 1 and <= 8 && size.Rows is >= 1 and <= 4);
            Assert.IsNotNull(WidgetCatalog.DefaultPlacement(id));
        }
    }
}
