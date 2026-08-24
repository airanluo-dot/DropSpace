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

/// <summary>
/// Describes evidence that a pointer gesture is an operating-system drag. This deliberately says
/// nothing about the payload type: accessibility drag-start events prove intent, not files.
/// </summary>
public enum DragIntentConfidence
{
    None,
    PointerThreshold,
    AccessibilityConfirmed,
    OleDragConfirmed,
}

/// <summary>Describes how far DropSpace has verified that a drag can safely be accepted.</summary>
public enum PayloadConfidence
{
    Unknown,
    FileLike,
    FileVerified,
}

public enum DragSessionState
{
    Idle,
    PointerCandidate,
    ProbePending,
    SpeculativeReveal,
    VisibleTargetActive,
    VerifiedFileDrag,
    AwaitingOleCompletion,
    Completed,
    Cancelled,
    Rejected,
    TimedOut,
}
