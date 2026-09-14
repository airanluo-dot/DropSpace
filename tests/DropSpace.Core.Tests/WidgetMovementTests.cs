using DropSpace.Core.Widgets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class WidgetMovementTests
{
    [TestMethod]
    public void EmptyMovePreservesEveryOtherPlacementAndOrder()
    {
        var layout = WidgetLayout.Default;
        var desired = layout.Expanded[0] with { Column = 4, Row = 3 };
        Assert.IsTrue(WidgetLayoutPolicy.TryMoveOrResize(layout, desired, out var result));
        Assert.AreEqual(desired, result.Expanded[0]);
        Assert.IsTrue(layout.Expanded.Skip(1).SequenceEqual(result.Expanded.Skip(1)));
    }

    [TestMethod]
    public void EqualSizedWidgetsSwapWithoutReflow()
    {
        var layout = WidgetLayout.Default;
        var clock = layout.Expanded[0];
        var resources = layout.Expanded[2];
        Assert.IsTrue(WidgetLayoutPolicy.TryMoveOrResize(layout, clock with { Column = resources.Column, Row = resources.Row }, out var result));
        Assert.AreEqual(resources with { Column = clock.Column, Row = clock.Row }, result.Expanded[2]);
        Assert.IsTrue(layout.Expanded.Where(p => p.Id != clock.Id && p.Id != resources.Id).SequenceEqual(result.Expanded.Where(p => p.Id != clock.Id && p.Id != resources.Id)));
    }

    [TestMethod]
    [DataRow(0, 0, 2, 2)]
    [DataRow(7, 3, 2, 1)]
    [DataRow(2, 0, 2, 1)]
    public void InvalidDropOrResizePreservesLayout(int column, int row, int width, int height)
    {
        var layout = WidgetLayout.Default;
        Assert.IsFalse(WidgetLayoutPolicy.TryMoveOrResize(layout, layout.Expanded[0] with { Column = column, Row = row, ColumnSpan = width, RowSpan = height }, out var result));
        Assert.AreSame(layout, result);
    }
}

