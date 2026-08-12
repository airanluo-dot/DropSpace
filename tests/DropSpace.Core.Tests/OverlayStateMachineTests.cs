using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayStateMachineTests
{
    [TestMethod]
    public void EmptyIdleRestoresHidden()
    {
        var machine = Create(0);
        Assert.AreEqual(OverlayState.Hidden, machine.Snapshot.State);
    }

    [TestMethod]
    public void EmptyDragApproachBecomesReadyThenCompactAfterDrop()
    {
        var machine = Create(0);
        machine.BeginDragApproach();
        Assert.AreEqual(OverlayState.DragApproaching, machine.Snapshot.State);
        machine.SetDragReady(true);
        Assert.AreEqual(OverlayState.DragReady, machine.Snapshot.State);
        machine.CompleteDrop(1);
        Assert.AreEqual(OverlayState.Compact, machine.Snapshot.State);
    }

    [TestMethod]
    public void ExistingItemRestoresCompact()
    {
        var machine = Create(1);
        Assert.AreEqual(OverlayState.Compact, machine.Snapshot.State);
    }

    [TestMethod]
    public void RemovingLastItemDismissesThenHides()
    {
        var machine = Create(1);
        machine.SetTemporaryItemCount(0);
        Assert.AreEqual(OverlayState.Dismissing, machine.Snapshot.State);
        machine.CompleteDismissal();
        Assert.AreEqual(OverlayState.Hidden, machine.Snapshot.State);
    }

    [TestMethod]
    public void EmptyCancelledDragReturnsThroughDismissalToHidden()
    {
        var machine = Create(0);
        machine.BeginDragApproach();
        machine.CancelDrag();
        Assert.AreEqual(OverlayState.Dismissing, machine.Snapshot.State);
        machine.CompleteDismissal();
        Assert.AreEqual(OverlayState.Hidden, machine.Snapshot.State);
    }

    [TestMethod]
    public void ExistingItemsCancelledDragReturnsCompact()
    {
        var machine = Create(2);
        machine.BeginDragApproach();
        machine.CancelDrag();
        Assert.AreEqual(OverlayState.Compact, machine.Snapshot.State);
    }

    [TestMethod]
    public void CompactExpandsAndCollapses()
    {
        var machine = Create(1);
        machine.Expand();
        Assert.AreEqual(OverlayState.Expanded, machine.Snapshot.State);
        machine.Collapse();
        Assert.AreEqual(OverlayState.Compact, machine.Snapshot.State);
    }

    [TestMethod]
    public void CompactVisibleDragUsesDragReadyAndReturnsCompact()
    {
        var machine = Create(1);

        machine.BeginVisibleDrag();
        machine.SetDragReady(true);
        Assert.AreEqual(OverlayState.DragReady, machine.Snapshot.State);
        machine.CompleteVisibleDrop(2);

        Assert.AreEqual(OverlayState.Compact, machine.Snapshot.State);
        Assert.AreEqual(2, machine.Snapshot.TemporaryItemCount);
    }

    [TestMethod]
    public void ExpandedVisibleDragKeepsGeometryAndReturnsExpandedAfterDrop()
    {
        var machine = Create(1);
        machine.Expand();

        machine.BeginVisibleDrag();
        machine.SetDragReady(true);
        Assert.AreEqual(OverlayState.Expanded, machine.Snapshot.State);
        Assert.IsTrue(machine.Snapshot.ExpandedDropActive);
        machine.CompleteVisibleDrop(2);

        Assert.AreEqual(OverlayState.Expanded, machine.Snapshot.State);
        Assert.IsFalse(machine.Snapshot.ExpandedDropActive);
        Assert.AreEqual(2, machine.Snapshot.TemporaryItemCount);
    }

    [TestMethod]
    public void ExpandedVisibleDragLeaveRestoresExpandedWithoutChangingCount()
    {
        var machine = Create(3);
        machine.Expand();
        machine.BeginVisibleDrag();
        machine.CancelDrag();

        Assert.AreEqual(OverlayState.Expanded, machine.Snapshot.State);
        Assert.IsFalse(machine.Snapshot.ExpandedDropActive);
        Assert.AreEqual(3, machine.Snapshot.TemporaryItemCount);
    }

    [TestMethod]
    public void DismissalCanBeInterruptedByDrag()
    {
        var machine = Create(1);
        machine.SetTemporaryItemCount(0);
        machine.BeginDragApproach();
        Assert.AreEqual(OverlayState.DragApproaching, machine.Snapshot.State);
    }

    [TestMethod]
    public void NewItemInterruptsDismissalAndReturnsCompact()
    {
        var machine = Create(1);
        machine.SetTemporaryItemCount(0);
        machine.SetTemporaryItemCount(1);
        Assert.AreEqual(OverlayState.Compact, machine.Snapshot.State);
    }

    [TestMethod]
    public void OneHundredRevealAndExpandCyclesReturnToIdleWithoutAFrameLoop()
    {
        var machine = Create(0);
        var notifications = 0;
        machine.Changed += (_, _) => notifications++;

        for (var index = 0; index < 100; index++)
        {
            machine.BeginDragApproach();
            machine.SetDragReady(true);
            machine.CompleteDrop(1);
            machine.Expand();
            machine.Collapse();
            machine.SetTemporaryItemCount(0);
            machine.CompleteDismissal();
        }

        Assert.AreEqual(OverlayState.Hidden, machine.Snapshot.State);
        Assert.AreEqual(700, notifications);
    }

    private static OverlayStateMachine Create(int count)
    {
        var machine = new OverlayStateMachine();
        machine.Restore(count);
        return machine;
    }
}
