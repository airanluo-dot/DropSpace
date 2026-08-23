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
    public void UnknownThresholdCreatesGenericCandidateRequiringProbe()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);

        var transition = policy.PointerMoved(new(300, 300));

        Assert.AreEqual(DragSessionTransitionKind.Started, transition.Kind);
        Assert.AreEqual(DragEvidenceLevel.GenericCandidate, transition.EvidenceLevel);
        Assert.IsTrue(transition.RequiresOleVerification);
        Assert.IsTrue(policy.IsActive);
    }

    [TestMethod]
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
    public void RecognizedShellSurfaceWithoutExactItemStillRequiresOleVerification()
    {
        var policy = Create();
        policy.PointerPressed(
            new(100, 100),
            DragPointerButton.Left,
            DragSourceKind.ExplorerFileView,
            exactFileItem: false);

        var transition = policy.PointerMoved(new(120, 100));

        Assert.AreEqual(DragSessionTransitionKind.Started, transition.Kind);
        Assert.AreEqual(DragEvidenceLevel.GenericCandidate, transition.EvidenceLevel);
        Assert.IsTrue(transition.RequiresOleVerification);
        Assert.IsTrue(transition.Evidence.HasFlag(DragEvidenceFlags.TrustedFileSurface));
        Assert.IsFalse(transition.Evidence.HasFlag(DragEvidenceFlags.ExactFileItem));
    }

    [TestMethod]
    public void AccessibilitySignalIsDeduplicatedWithMouseFallback()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.ExplorerFileView);
        var first = policy.AccessibilityDragStarted(new(105, 105), DragSourceKind.ExplorerFileView);
        var duplicate = policy.PointerMoved(new(120, 120));

        Assert.AreEqual(DragSessionTransitionKind.Started, first.Kind);
        Assert.AreEqual(DragSessionTransitionKind.None, duplicate.Kind);
    }

    [TestMethod]
    public void AccessibilitySignalKeepsVerifiedPressOriginAfterPointerLeavesItem()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.ExplorerFileView);

        var transition = policy.AccessibilityDragStarted(new(180, 180), DragSourceKind.Unknown);

        Assert.AreEqual(DragSessionTransitionKind.Started, transition.Kind);
        Assert.AreEqual(DragSourceKind.ExplorerFileView, transition.Source);
    }

    [TestMethod]
    public void AccessibilityDragStartAllowsUnknownProviderWithoutProcessHardcoding()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);

        var transition = policy.AccessibilityDragStarted(new(120, 120), DragSourceKind.Unknown);

        Assert.AreEqual(DragSessionTransitionKind.Started, transition.Kind);
        Assert.AreEqual(DragEvidenceLevel.Strong, transition.EvidenceLevel);
        Assert.IsFalse(transition.RequiresOleVerification);
        Assert.IsTrue(policy.IsActive);
    }

    [TestMethod]
    public void StrongSignalPromotesGenericCandidateAndMakesStaleProbeFailureHarmless()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);
        var started = policy.PointerMoved(new(120, 100));

        var promoted = policy.AccessibilityDragStarted(new(125, 100), DragSourceKind.Unknown);
        var staleRejection = policy.ProbeRejected(started.SessionId, new(130, 100));
        var staleTimeout = policy.ProbeTimedOut(started.SessionId, new(130, 100));

        Assert.AreEqual(DragSessionTransitionKind.Verified, promoted.Kind);
        Assert.AreEqual(DragEvidenceLevel.Strong, promoted.EvidenceLevel);
        Assert.IsFalse(promoted.RequiresOleVerification);
        Assert.AreEqual(DragSessionTransitionKind.None, staleRejection.Kind);
        Assert.AreEqual(DragSessionTransitionKind.None, staleTimeout.Kind);
        Assert.IsTrue(policy.IsActive);
    }

    [TestMethod]
    public void DocumentedObjectDragSignalCanPromoteARecognizedShellSurface()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);

        var transition = policy.AccessibilityDragStarted(
            new(140, 120),
            DragSourceKind.ExplorerFileView);

        Assert.AreEqual(DragSessionTransitionKind.Started, transition.Kind);
        Assert.AreEqual(DragSourceKind.ExplorerFileView, transition.Source);
        Assert.IsTrue(policy.IsActive);
    }

    [TestMethod]
    public void ProbeVerifiedFileCommitsSpeculativeSessionWithoutRestartingIt()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);

        var started = policy.PointerMoved(new(140, 120));
        var transition = policy.ProbeVerified(started.SessionId, new(145, 125));

        Assert.AreEqual(DragSessionTransitionKind.Verified, transition.Kind);
        Assert.AreEqual(started.SessionId, transition.SessionId);
        Assert.AreEqual(DragEvidenceLevel.VerifiedFile, transition.EvidenceLevel);
        Assert.IsFalse(transition.RequiresOleVerification);
        Assert.IsTrue(policy.IsActive);
    }

    [TestMethod]
    public void ProbeRejectCancelsGenericCandidate()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);
        var started = policy.PointerMoved(new(140, 120));

        var rejected = policy.ProbeRejected(started.SessionId, new(145, 125));

        Assert.AreEqual(DragSessionTransitionKind.Rejected, rejected.Kind);
        Assert.IsFalse(policy.IsActive);
    }

    [TestMethod]
    public void ProbeTimeoutCancelsOnlyGenericCandidate()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);
        var started = policy.PointerMoved(new(140, 120));

        var timedOut = policy.ProbeTimedOut(started.SessionId, new(145, 125));

        Assert.AreEqual(DragSessionTransitionKind.TimedOut, timedOut.Kind);
        Assert.IsFalse(policy.IsActive);
    }

    [TestMethod]
    public void ReleaseWhileProbePendingCancelsSession()
    {
        var policy = Create();
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);
        var started = policy.PointerMoved(new(140, 120));

        var released = policy.PointerReleased(new(145, 125));

        Assert.AreEqual(started.SessionId, released.SessionId);
        Assert.AreEqual(DragSessionTransitionKind.Cancelled, released.Kind);
        Assert.IsFalse(policy.IsActive);
    }

    [TestMethod]
    public void OldTimeoutCannotCancelNewSession()
    {
        var policy = Create();
        policy.PointerPressed(new(10, 10), DragPointerButton.Left, DragSourceKind.Unknown);
        var first = policy.PointerMoved(new(30, 10));
        _ = policy.DragCancelled(new(30, 10));
        policy.PointerPressed(new(100, 100), DragPointerButton.Left, DragSourceKind.Unknown);
        var second = policy.PointerMoved(new(120, 100));

        var staleTimeout = policy.Timeout(first.SessionId, new(30, 10));

        Assert.AreEqual(DragSessionTransitionKind.None, staleTimeout.Kind);
        Assert.AreEqual(second.SessionId, policy.ActiveSessionId);
        Assert.IsTrue(policy.IsActive);
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
