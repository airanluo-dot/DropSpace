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
    Completed,
    Cancelled,
}

public readonly record struct DragScreenPoint(int X, int Y);

public readonly record struct DragSessionTransition(
    DragSessionTransitionKind Kind,
    long SessionId,
    DragScreenPoint Point,
    DragSourceKind Source)
{
    public static DragSessionTransition None { get; } = new(
        DragSessionTransitionKind.None,
        0,
        default,
        DragSourceKind.Unknown);
}

/// <summary>
/// Pure, testable gate for candidate file-drag sessions. This policy never reads files and never
/// accepts a drop; it only combines trusted source classification, the configured Windows drag
/// threshold and optional accessibility drag events into one deduplicated candidate session.
/// OLE IDataObject/CF_HDROP remains the final authority in the App layer.
/// </summary>
public sealed class DragSessionPolicy
{
    private readonly int _horizontalThreshold;
    private readonly int _verticalThreshold;
    private bool _pointerDown;
    private bool _active;
    private DragScreenPoint _origin;
    private DragSourceKind _source;
    private long _nextSessionId;
    private long _activeSessionId;

    public DragSessionPolicy(int horizontalThreshold, int verticalThreshold)
    {
        _horizontalThreshold = Math.Max(1, horizontalThreshold);
        _verticalThreshold = Math.Max(1, verticalThreshold);
    }

    public bool IsActive => _active;

    public long ActiveSessionId => _activeSessionId;

    public void PointerPressed(
        DragScreenPoint point,
        DragPointerButton button,
        DragSourceKind source)
    {
        if (_active)
        {
            return;
        }

        _pointerDown = true;
        _origin = point;
        _source = source;
    }

    public DragSessionTransition PointerMoved(DragScreenPoint point)
    {
        if (_active || !_pointerDown || !IsSupportedFileSource(_source))
        {
            return DragSessionTransition.None;
        }

        var thresholdCrossed = Math.Abs(point.X - _origin.X) >= _horizontalThreshold ||
                               Math.Abs(point.Y - _origin.Y) >= _verticalThreshold;
        if (!thresholdCrossed)
        {
            return DragSessionTransition.None;
        }

        // Explorer/Desktop file views are the bounded fallback for providers that omit UIA drag
        // events. UIA is retained as a strong signal but is deliberately not made mandatory.
        return Start(point);
    }

    public DragSessionTransition AccessibilityDragStarted(
        DragScreenPoint point,
        DragSourceKind source)
    {
        if (_active)
        {
            return DragSessionTransition.None;
        }

        if (IsSupportedFileSource(source))
        {
            _source = source;
        }
        else if (!IsSupportedFileSource(_source))
        {
            return DragSessionTransition.None;
        }
        return Start(point);
    }

    public DragSessionTransition UiAutomationDragStarted(
        DragScreenPoint point,
        DragSourceKind source) => AccessibilityDragStarted(point, source);

    public DragSessionTransition PointerReleased(DragScreenPoint point) =>
        Finish(point, DragSessionTransitionKind.Completed);

    public DragSessionTransition DragCompleted(DragScreenPoint point) =>
        Finish(point, DragSessionTransitionKind.Completed);

    public DragSessionTransition DragCancelled(DragScreenPoint point) =>
        Finish(point, DragSessionTransitionKind.Cancelled);

    public DragSessionTransition Timeout(DragScreenPoint point) =>
        Finish(point, DragSessionTransitionKind.Cancelled);

    public void Reset()
    {
        _pointerDown = false;
        _active = false;
        _source = DragSourceKind.Unknown;
        _activeSessionId = 0;
    }

    private DragSessionTransition Start(DragScreenPoint point)
    {
        _active = true;
        _activeSessionId = Interlocked.Increment(ref _nextSessionId);
        return new DragSessionTransition(
            DragSessionTransitionKind.Started,
            _activeSessionId,
            point,
            _source);
    }

    private DragSessionTransition Finish(
        DragScreenPoint point,
        DragSessionTransitionKind kind)
    {
        _pointerDown = false;
        if (!_active)
        {
            _source = DragSourceKind.Unknown;
            return DragSessionTransition.None;
        }

        var transition = new DragSessionTransition(kind, _activeSessionId, point, _source);
        _active = false;
        _source = DragSourceKind.Unknown;
        _activeSessionId = 0;
        return transition;
    }

    private static bool IsSupportedFileSource(DragSourceKind source) =>
        source is DragSourceKind.ExplorerFileView or DragSourceKind.DesktopFileView;
}
