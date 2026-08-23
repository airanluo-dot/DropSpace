namespace DropSpace.Core.DragDrop;

[Flags]
public enum DragEvidenceFlags
{
    None = 0,
    PointerPressed = 1 << 0,
    DragThresholdCrossed = 1 << 1,
    TrustedFileSurface = 1 << 2,
    ExactFileItem = 1 << 3,
    AccessibilityDragStarted = 1 << 4,
    OleVerifiedFile = 1 << 5,
}

public enum DragEvidenceLevel
{
    None,
    PointerCandidate,
    GenericCandidate,
    Strong,
    VerifiedFile,
}

public enum DragSessionState
{
    Idle,
    PointerCandidate,
    ProbePending,
    SpeculativeReveal,
    VisibleTargetActive,
    VerifiedFileDrag,
    Completed,
    Cancelled,
    Rejected,
    TimedOut,
}
