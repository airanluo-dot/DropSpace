namespace DropSpace.Core.DragDrop;

public enum DragPointerButton
{
    Left,
    Right,
}

public enum DragSourceKind
{
    Unknown,
    ExplorerFileView,
    DesktopFileView,
}

public enum DragSessionTransitionKind
{
    None,
    Started,
    Verified,
    AwaitingCompletion,
    Superseded,
    Completed,
    Cancelled,
    Rejected,
    TimedOut,
}

public readonly record struct DragScreenPoint(int X, int Y);

public readonly record struct DragSessionTransition(
    DragSessionTransitionKind Kind,
    long SessionId,
    DragScreenPoint Point,
    DragSourceKind Source,
    DragSessionState State = DragSessionState.Idle,
    DragEvidenceLevel EvidenceLevel = DragEvidenceLevel.None,
    DragEvidenceFlags Evidence = DragEvidenceFlags.None,
    bool RequiresOleVerification = false,
    DragIntentConfidence DragIntentConfidence = DragIntentConfidence.None,
    PayloadConfidence PayloadConfidence = PayloadConfidence.Unknown)
{
    public static DragSessionTransition None { get; } = new(
        DragSessionTransitionKind.None,
        0,
        default,
        DragSourceKind.Unknown,
        DragSessionState.Idle);
}

/// <summary>
/// Pure, testable gate for candidate file-drag sessions. This policy never reads files and never
/// accepts a drop; it only combines trusted source classification, the configured Windows drag
/// threshold and optional accessibility drag events into one deduplicated candidate session.
/// Unknown sources may form a speculative candidate, but they require a bounded OLE verification
/// probe before the session becomes a verified file drag. The policy never reads user data.
/// </summary>
public sealed class DragSessionPolicy
{
    private readonly int _horizontalThreshold;
    private readonly int _verticalThreshold;
    private bool _pointerDown;
    private bool _active;
    private DragScreenPoint _origin;
    private DragSourceKind _source;
    private DragSessionState _state;
    private DragEvidenceLevel _evidenceLevel;
    private DragEvidenceFlags _evidence;
    private bool _exactFileItem;
    private bool _requiresOleVerification;
    private DragIntentConfidence _intentConfidence;
    private PayloadConfidence _payloadConfidence;
    private long _nextSessionId;
    private long _activeSessionId;

    public DragSessionPolicy(int horizontalThreshold, int verticalThreshold)
    {
        _horizontalThreshold = Math.Max(1, horizontalThreshold);
        _verticalThreshold = Math.Max(1, verticalThreshold);
    }

    public bool IsActive => _active;

    public long ActiveSessionId => _activeSessionId;

    public DragSessionState ActiveState => _state;

    public DragEvidenceLevel ActiveEvidenceLevel => _evidenceLevel;

    public bool RequiresOleVerification => _requiresOleVerification;

    public DragIntentConfidence ActiveIntentConfidence => _intentConfidence;

    public PayloadConfidence ActivePayloadConfidence => _payloadConfidence;

    public DragSessionTransition PointerPressed(
        DragScreenPoint point,
        DragPointerButton button,
        DragSourceKind source,
        bool exactFileItem = true)
    {
        var superseded = DragSessionTransition.None;
        if (_active)
        {
            // A fresh press must never be swallowed by the previous session's completion grace.
            // Supersede it immediately; stale callbacks are rejected by their old session id.
            superseded = CreateTransition(DragSessionTransitionKind.Superseded, point);
            ClearActiveState();
        }

        _pointerDown = true;
        _origin = point;
        _source = source;
        _exactFileItem = exactFileItem && IsSupportedFileSource(source);
        _state = DragSessionState.PointerCandidate;
        _evidenceLevel = DragEvidenceLevel.PointerCandidate;
        _intentConfidence = DragIntentConfidence.None;
        _payloadConfidence = _exactFileItem ? PayloadConfidence.FileLike : PayloadConfidence.Unknown;
        _evidence = DragEvidenceFlags.PointerPressed;
        if (IsSupportedFileSource(source))
        {
            _evidence |= DragEvidenceFlags.TrustedFileSurface;
        }

        if (_exactFileItem)
        {
            _evidence |= DragEvidenceFlags.ExactFileItem;
        }

        _ = button;
        return superseded;
    }

    public DragSessionTransition PointerMoved(DragScreenPoint point)
    {
        if (_active || !_pointerDown)
        {
            return DragSessionTransition.None;
        }

        var thresholdCrossed = Math.Abs(point.X - _origin.X) >= _horizontalThreshold ||
                               Math.Abs(point.Y - _origin.Y) >= _verticalThreshold;
        if (!thresholdCrossed)
        {
            return DragSessionTransition.None;
        }

        _evidence |= DragEvidenceFlags.DragThresholdCrossed;
        _intentConfidence = DragIntentConfidence.PointerThreshold;
        var trustedFileCandidate = IsSupportedFileSource(_source) && _exactFileItem;
        return Start(
            point,
            trustedFileCandidate ? DragEvidenceLevel.Strong : DragEvidenceLevel.GenericCandidate,
            requiresOleVerification: !trustedFileCandidate);
    }

    public DragSessionTransition AccessibilityDragStarted(
        DragScreenPoint point,
        DragSourceKind source)
    {
        if (_active)
        {
            _evidence |= DragEvidenceFlags.AccessibilityDragStarted;
            _intentConfidence = DragIntentConfidence.AccessibilityConfirmed;
            _evidenceLevel = DragEvidenceLevel.Strong;
            // Accessibility confirms drag intent only. Unknown payloads must continue through the
            // bounded OLE probe before DropSpace activates the accepting target.
            if (_payloadConfidence == PayloadConfidence.Unknown)
            {
                _requiresOleVerification = true;
                _state = DragSessionState.SpeculativeReveal;
            }

            return DragSessionTransition.None;
        }

        if (IsSupportedFileSource(source))
        {
            _source = source;
        }
        _evidence |= DragEvidenceFlags.AccessibilityDragStarted;
        _intentConfidence = DragIntentConfidence.AccessibilityConfirmed;
        var fileLike = IsSupportedFileSource(_source) && _exactFileItem;
        _payloadConfidence = fileLike ? PayloadConfidence.FileLike : PayloadConfidence.Unknown;
        return Start(point, DragEvidenceLevel.Strong, requiresOleVerification: !fileLike);
    }

    public DragSessionTransition PointerReleased(DragScreenPoint point)
    {
        if (!_active)
        {
            Reset();
            return DragSessionTransition.None;
        }

        _pointerDown = false;
        _state = DragSessionState.AwaitingOleCompletion;
        return CreateTransition(DragSessionTransitionKind.AwaitingCompletion, point);
    }

    public DragSessionTransition DragCompleted(DragScreenPoint point) =>
        Finish(point, DragSessionTransitionKind.Completed, DragSessionState.Completed);

    public DragSessionTransition DragCancelled(DragScreenPoint point) =>
        Finish(point, DragSessionTransitionKind.Cancelled, DragSessionState.Cancelled);

    public DragSessionTransition CompletionGraceExpired(long sessionId, DragScreenPoint point) =>
        _active && _activeSessionId == sessionId && _state == DragSessionState.AwaitingOleCompletion
            ? Finish(point, DragSessionTransitionKind.Cancelled, DragSessionState.Cancelled)
            : DragSessionTransition.None;

    public DragSessionTransition ProbeVerified(long sessionId, DragScreenPoint point)
    {
        if (!_active || _activeSessionId != sessionId)
        {
            return DragSessionTransition.None;
        }

        _requiresOleVerification = false;
        _state = DragSessionState.VerifiedFileDrag;
        _evidenceLevel = DragEvidenceLevel.VerifiedFile;
        _evidence |= DragEvidenceFlags.OleVerifiedFile;
        _intentConfidence = DragIntentConfidence.OleDragConfirmed;
        _payloadConfidence = PayloadConfidence.FileVerified;
        return CreateTransition(DragSessionTransitionKind.Verified, point);
    }

    public DragSessionTransition ProbeRejected(long sessionId, DragScreenPoint point) =>
        _active && _activeSessionId == sessionId && _requiresOleVerification
            ? Finish(point, DragSessionTransitionKind.Rejected, DragSessionState.Rejected)
            : DragSessionTransition.None;

    public DragSessionTransition ProbeTimedOut(long sessionId, DragScreenPoint point) =>
        _active && _activeSessionId == sessionId && _requiresOleVerification
            ? Finish(point, DragSessionTransitionKind.TimedOut, DragSessionState.TimedOut)
            : DragSessionTransition.None;

    public DragSessionTransition Timeout(long sessionId, DragScreenPoint point) =>
        _active && _activeSessionId == sessionId
            ? Finish(point, DragSessionTransitionKind.TimedOut, DragSessionState.TimedOut)
            : DragSessionTransition.None;

    public void Reset()
    {
        _pointerDown = false;
        _active = false;
        _source = DragSourceKind.Unknown;
        _state = DragSessionState.Idle;
        _evidenceLevel = DragEvidenceLevel.None;
        _evidence = DragEvidenceFlags.None;
        _exactFileItem = false;
        _requiresOleVerification = false;
        _intentConfidence = DragIntentConfidence.None;
        _payloadConfidence = PayloadConfidence.Unknown;
        _activeSessionId = 0;
    }

    private DragSessionTransition Start(
        DragScreenPoint point,
        DragEvidenceLevel evidenceLevel,
        bool requiresOleVerification)
    {
        _active = true;
        _activeSessionId = Interlocked.Increment(ref _nextSessionId);
        _evidenceLevel = evidenceLevel;
        _requiresOleVerification = requiresOleVerification;
        _state = requiresOleVerification
            ? DragSessionState.SpeculativeReveal
            : DragSessionState.VisibleTargetActive;
        return CreateTransition(DragSessionTransitionKind.Started, point);
    }

    private DragSessionTransition Finish(
        DragScreenPoint point,
        DragSessionTransitionKind kind,
        DragSessionState finalState)
    {
        _pointerDown = false;
        if (!_active)
        {
            Reset();
            return DragSessionTransition.None;
        }

        _state = finalState;
        var transition = CreateTransition(kind, point);
        ClearActiveState();
        return transition;
    }

    private DragSessionTransition CreateTransition(
        DragSessionTransitionKind kind,
        DragScreenPoint point) =>
        new(
            kind,
            _activeSessionId,
            point,
            _source,
            _state,
            _evidenceLevel,
            _evidence,
            _requiresOleVerification,
            _intentConfidence,
            _payloadConfidence);

    private void ClearActiveState()
    {
        _pointerDown = false;
        _active = false;
        _source = DragSourceKind.Unknown;
        _state = DragSessionState.Idle;
        _evidenceLevel = DragEvidenceLevel.None;
        _evidence = DragEvidenceFlags.None;
        _exactFileItem = false;
        _requiresOleVerification = false;
        _intentConfidence = DragIntentConfidence.None;
        _payloadConfidence = PayloadConfidence.Unknown;
        _activeSessionId = 0;
    }

    private static bool IsSupportedFileSource(DragSourceKind source) =>
        source is DragSourceKind.ExplorerFileView or DragSourceKind.DesktopFileView;
}
