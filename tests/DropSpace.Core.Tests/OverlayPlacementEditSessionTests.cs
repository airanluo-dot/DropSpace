using DropSpace.Core.DragDrop;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayPlacementEditSessionTests
{
    [TestMethod]
    public void ArmedSessionUsesPhysicalPointerDeltaConvertedToDips()
    {
        var session = new OverlayPlacementEditSession();
        session.Arm(new OverlayCustomPlacement(500, 40));

        Assert.IsTrue(session.TryBeginDrag(new DragScreenPoint(100, 200)));
        var preview = session.Move(new DragScreenPoint(250, 320), 1.5);

        Assert.AreEqual(600, preview.X, 0.001);
        Assert.AreEqual(120, preview.Y, 0.001);
        Assert.AreEqual(OverlayPlacementEditState.Dragging, session.State);
    }

    [TestMethod]
    public void ReleaseCommitsOnlyTheFinalPreview()
    {
        var session = new OverlayPlacementEditSession();
        session.Arm(new OverlayCustomPlacement(300, 8));
        session.TryBeginDrag(new DragScreenPoint(0, 0));
        session.Move(new DragScreenPoint(30, 45), 1);

        var committed = session.Commit();

        Assert.AreEqual(new OverlayCustomPlacement(330, 53), committed);
        Assert.AreEqual(OverlayPlacementEditState.Inactive, session.State);
    }

    [TestMethod]
    public void EscapeRestoresSnapshotWithoutChangingTheSavedCandidate()
    {
        var session = new OverlayPlacementEditSession();
        session.Arm(new OverlayCustomPlacement(300, 8));
        session.TryBeginDrag(new DragScreenPoint(50, 50));
        session.Move(new DragScreenPoint(200, 100), 2);

        var restored = session.Cancel();

        Assert.AreEqual(new OverlayCustomPlacement(300, 8), restored);
        Assert.AreEqual(new OverlayCustomPlacement(300, 8), session.Preview);
        Assert.AreEqual(OverlayPlacementEditState.Inactive, session.State);
    }
}
