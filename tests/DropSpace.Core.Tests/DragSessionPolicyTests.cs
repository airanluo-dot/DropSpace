using DropSpace.Core.DragDrop;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class DragSessionPolicyTests
{
    [TestMethod]
    public void ClickAndLongPressWithoutMovementDoNotStart()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.ExplorerFileView);

        Assert.AreEqual(DragSessionTransitionKind.None, policy.PointerMoved(new(103, 103)).Kind);
        Assert.AreEqual(DragSessionTransitionKind.None, policy.PointerReleased(new(103, 103)).Kind);
        Assert.IsFalse(policy.IsActive);
    }

    [TestMethod]
    public void UnknownSourcesNeverStartAfterThreshold()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);

        Assert.AreEqual(DragSessionTransitionKind.None, policy.PointerMoved(new(300, 300)).Kind);
        Assert.IsFalse(policy.IsActive);
    }

    [DataTestMethod]
    [DataRow(DragSourceKind.ExplorerFileView, DragPointerButton.Left)]
    [DataRow(DragSourceKind.ExplorerFileView, DragPointerButton.Right)]
    [DataRow(DragSourceKind.DesktopFileView, DragPointerButton.Left)]
    [DataRow(DragSourceKind.DesktopFileView, DragPointerButton.Right)]
    public void ExplorerAndDesktopFileDragsStartAfterSystemThreshold(
        DragSourceKind source,
        DragPointerButton button)
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), button, source);

        var transition = policy.PointerMoved(new(109, 100));

        Assert.AreEqual(DragSessionTransitionKind.Started, transition.Kind);
        Assert.AreEqual(source, transition.Source);
        Assert.IsTrue(policy.IsActive);
    }

    [TestMethod]
    public void UiAutomationSignalIsDeduplicatedWithMouseFallback()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.ExplorerFileView);
        var first = policy.UiAutomationDragStarted(new(105, 105), DragSourceKind.ExplorerFileView);
        var duplicate = policy.PointerMoved(new(120, 120));

        Assert.AreEqual(DragSessionTransitionKind.Started, first.Kind);
        Assert.AreEqual(DragSessionTransitionKind.None, duplicate.Kind);
    }

    [TestMethod]
    public void UiAutomationSignalKeepsVerifiedPressOriginAfterPointerLeavesItem()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.ExplorerFileView);

        var transition = policy.UiAutomationDragStarted(new(180, 180), DragSourceKind.Unknown);

        Assert.AreEqual(DragSessionTransitionKind.Started, transition.Kind);
        Assert.AreEqual(DragSourceKind.ExplorerFileView, transition.Source);
    }

    [TestMethod]
    public void EscapeCancellationClosesExactlyOneSession()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.DesktopFileView);
        var started = policy.PointerMoved(new(120, 100));
        var cancelled = policy.DragCancelled(new(130, 100));
        var duplicate = policy.DragCancelled(new(140, 100));

        Assert.AreEqual(started.SessionId, cancelled.SessionId);
        Assert.AreEqual(DragSessionTransitionKind.Cancelled, cancelled.Kind);
        Assert.AreEqual(DragSessionTransitionKind.None, duplicate.Kind);
        Assert.IsFalse(policy.IsActive);
    }

    [TestMethod]
    public void OneThousandSessionsLeaveNoActiveState()
    {
        var policy = Create();
        var lastSession = 0L;
        for (var index = 0; index < 1_000; index++)
        {
            policy.PointerPressed(new(10, 10), DragPointerButton.Left, DragSourceKind.ExplorerFileView);
            var started = policy.PointerMoved(new(20, 10));
            Assert.IsTrue(started.SessionId > lastSession);
            lastSession = started.SessionId;
            var finished = index % 2 == 0
                ? policy.DragCompleted(new(20, 10))
                : policy.DragCancelled(new(20, 10));
            Assert.AreEqual(started.SessionId, finished.SessionId);
        }

        Assert.IsFalse(policy.IsActive);
        Assert.AreEqual(0L, policy.ActiveSessionId);
    }

    private static DragSessionPolicy Create() => new(8, 8);
}
