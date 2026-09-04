using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayStateDedupeTests
{
    [TestMethod]
    public void RepeatedDragReadyDoesNotPublishSemanticDuplicates()
    {
        var machine = new OverlayStateMachine();
        var notifications = 0;
        machine.Changed += (_, _) => notifications++;

        machine.Restore(0);
        machine.BeginDragApproach();
        var revisionBeforeReady = machine.Snapshot.Revision;
        for (var index = 0; index < 1_000; index++)
        {
            machine.SetDragReady(true);
        }

        Assert.AreEqual(OverlayState.DragReady, machine.Snapshot.State);
        Assert.AreEqual(revisionBeforeReady + 1, machine.Snapshot.Revision);
        Assert.AreEqual(3, notifications);
    }

    [TestMethod]
    public void RepeatedExpandedDragTargetDoesNotPublishSemanticDuplicates()
    {
        var machine = new OverlayStateMachine();
        machine.Restore(1);
        machine.Expand();
        machine.BeginVisibleDrag();
        var revision = machine.Snapshot.Revision;

        for (var index = 0; index < 1_000; index++)
        {
            machine.BeginVisibleDrag();
            machine.SetDragReady(true);
        }

        Assert.IsTrue(machine.Snapshot.ExpandedDropActive);
        Assert.AreEqual(revision, machine.Snapshot.Revision);
    }
}
