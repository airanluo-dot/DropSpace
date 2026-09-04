using DropSpace.Core.Models;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayTransitionTests
{
    [TestMethod]
    public void StateSnapshotCarriesCauseAndMotionPreference()
    {
        var machine = new OverlayStateMachine();
        machine.SetMotionPreference(OverlayMotionPreference.Reduced);
        machine.Restore(1);
        machine.Expand();

        var transition = machine.Snapshot.Transition;
        Assert.IsNotNull(transition);
        Assert.AreEqual(OverlayState.Compact, transition.From);
        Assert.AreEqual(OverlayState.Expanded, transition.To);
        Assert.AreEqual(OverlayTransitionCause.Expanded, transition.Cause);
        Assert.AreEqual(OverlayMotionPreference.Reduced, transition.MotionPreference);
    }

    [TestMethod]
    public void RepeatedExpandedDropPublishesAnInterruptibleTargetEntry()
    {
        var machine = new OverlayStateMachine();
        machine.Restore(1);
        machine.Expand();
        machine.BeginVisibleDrag();

        Assert.IsTrue(machine.Snapshot.ExpandedDropActive);
        Assert.AreEqual(OverlayState.Expanded, machine.Snapshot.Transition!.From);
        Assert.AreEqual(OverlayTransitionCause.DropTargetEntered, machine.Snapshot.Transition.Cause);
    }
}
