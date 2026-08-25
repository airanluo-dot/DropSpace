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
        session.Arm(new OverlayCustomPlacement(500, 40), new OverlayCustomPlacement(500, 40));

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
        session.Arm(new OverlayCustomPlacement(300, 8), new OverlayCustomPlacement(300, 8));
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
        session.Arm(new OverlayCustomPlacement(300, 8), new OverlayCustomPlacement(300, 8));
        session.TryBeginDrag(new DragScreenPoint(50, 50));
        session.Move(new DragScreenPoint(200, 100), 2);

        var restored = session.Cancel();

        Assert.AreEqual(new OverlayCustomPlacement(300, 8), restored);
        Assert.AreEqual(new OverlayCustomPlacement(300, 8), session.Preview);
        Assert.AreEqual(OverlayPlacementEditState.Inactive, session.State);
    }

    [TestMethod]
    public void FirstMoveUsesClampedProjectionAsDragOrigin()
    {
        var session = new OverlayPlacementEditSession();
        session.Arm(new OverlayCustomPlacement(1800, 40), new OverlayCustomPlacement(1200, 40));

        session.TryBeginDrag(new DragScreenPoint(100, 200));
        var preview = session.Move(new DragScreenPoint(200, 200), 1);

        Assert.AreEqual(new OverlayCustomPlacement(1300, 40), preview);
    }

    [TestMethod]
    public void CancelRestoresSavedOriginalAfterProjectedDrag()
    {
        var session = new OverlayPlacementEditSession();
        session.Arm(new OverlayCustomPlacement(1800, 40), new OverlayCustomPlacement(1200, 40));

        session.TryBeginDrag(new DragScreenPoint(100, 200));
        session.Move(new DragScreenPoint(200, 200), 1);

        Assert.AreEqual(new OverlayCustomPlacement(1800, 40), session.Cancel());
    }

    [TestMethod]
    public void CommitReturnsPreviewFromProjectedDrag()
    {
        var session = new OverlayPlacementEditSession();
        session.Arm(new OverlayCustomPlacement(1000, 40), new OverlayCustomPlacement(1000, 40));

        session.TryBeginDrag(new DragScreenPoint(100, 200));
        session.Move(new DragScreenPoint(200, 200), 1);

        Assert.AreEqual(new OverlayCustomPlacement(1100, 40), session.Commit());
    }
}
