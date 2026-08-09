using DropSpace.Core.Models;

namespace DropSpace.Core.Overlay;

public enum OverlayState
{
    Hidden,
    DragApproaching,
    DragReady,
    Compact,
    Expanded,
    Dismissing,
    ModeTransition,
}

public sealed record OverlaySnapshot(
    OverlayState State,
    int TemporaryItemCount,
    OverlayDisplayMode DisplayMode,
    OverlayDisplayMode TargetDisplayMode,
    bool ExpandedDropActive,
    long Revision);

public sealed class OverlayStateMachine
{
    private OverlayState _state = OverlayState.Hidden;
    private OverlayState _resumeAfterModeTransition = OverlayState.Hidden;
    private int _temporaryItemCount;
    private OverlayDisplayMode _displayMode = OverlayDisplayMode.DynamicIsland;
    private OverlayDisplayMode _targetDisplayMode = OverlayDisplayMode.DynamicIsland;
    private bool _expandedDropActive;
    private long _revision;

    public event EventHandler<OverlaySnapshot>? Changed;

    public OverlaySnapshot Snapshot => new(
        _state,
        _temporaryItemCount,
        _displayMode,
        _targetDisplayMode,
        _expandedDropActive,
        _revision);

    public void Restore(int temporaryItemCount, OverlayDisplayMode displayMode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryItemCount);
        _temporaryItemCount = temporaryItemCount;
        _displayMode = displayMode;
        _targetDisplayMode = displayMode;
        _expandedDropActive = false;
        _state = temporaryItemCount == 0 ? OverlayState.Hidden : OverlayState.Compact;
        _resumeAfterModeTransition = _state;
        Publish();
    }

    public void SetTemporaryItemCount(int temporaryItemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryItemCount);
        _temporaryItemCount = temporaryItemCount;

        if (temporaryItemCount == 0)
        {
            _expandedDropActive = false;
            if (_state is OverlayState.Compact or OverlayState.Expanded)
            {
                _state = OverlayState.Dismissing;
            }
        }
        else if (_state is OverlayState.Hidden or OverlayState.Dismissing)
        {
            _state = OverlayState.Compact;
        }

        Publish();
    }

    public void BeginDragApproach()
    {
        if (_state == OverlayState.ModeTransition)
        {
            _displayMode = _targetDisplayMode;
        }

        _state = OverlayState.DragApproaching;
        _expandedDropActive = false;
        Publish();
    }

    public void BeginVisibleDrag()
    {
        if (_state == OverlayState.Expanded)
        {
            _expandedDropActive = true;
            Publish();
            return;
        }

        BeginDragApproach();
    }

    public void SetDragReady(bool ready)
    {
        if (_expandedDropActive)
        {
            Publish();
            return;
        }

        if (_state is not (OverlayState.DragApproaching or OverlayState.DragReady))
        {
            return;
        }

        _state = ready ? OverlayState.DragReady : OverlayState.DragApproaching;
        Publish();
    }

    public void CancelDrag()
    {
        if (_expandedDropActive)
        {
            _expandedDropActive = false;
            Publish();
            return;
        }

        if (_state is not (OverlayState.DragApproaching or OverlayState.DragReady))
        {
            return;
        }

        _state = _temporaryItemCount == 0 ? OverlayState.Dismissing : OverlayState.Compact;
        Publish();
    }

    public void CompleteDrop(int temporaryItemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryItemCount);
        _temporaryItemCount = temporaryItemCount;
        _expandedDropActive = false;
        _state = temporaryItemCount == 0 ? OverlayState.Dismissing : OverlayState.Compact;
        Publish();
    }

    public void CompleteVisibleDrop(int temporaryItemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryItemCount);
        _temporaryItemCount = temporaryItemCount;
        var remainExpanded = _expandedDropActive;
        _expandedDropActive = false;
        _state = temporaryItemCount == 0
            ? OverlayState.Dismissing
            : remainExpanded ? OverlayState.Expanded : OverlayState.Compact;
        Publish();
    }

    public void Expand()
    {
        if (_temporaryItemCount > 0 && _state == OverlayState.Compact)
        {
            _state = OverlayState.Expanded;
            Publish();
        }
    }

    public void Collapse()
    {
        if (_state != OverlayState.Expanded)
        {
            return;
        }

        _state = _temporaryItemCount == 0 ? OverlayState.Dismissing : OverlayState.Compact;
        _expandedDropActive = false;
        Publish();
    }

    public void RequestDisplayMode(OverlayDisplayMode displayMode)
    {
        if (_displayMode == displayMode && _state != OverlayState.ModeTransition)
        {
            return;
        }

        _resumeAfterModeTransition = _state == OverlayState.ModeTransition
            ? _resumeAfterModeTransition
            : _state;
        _targetDisplayMode = displayMode;
        _state = OverlayState.ModeTransition;
        Publish();
    }

    public void CompleteModeTransition()
    {
        if (_state != OverlayState.ModeTransition)
        {
            return;
        }

        _displayMode = _targetDisplayMode;
        _state = ResolveResumeState();
        Publish();
    }

    public void CompleteDismissal()
    {
        if (_state != OverlayState.Dismissing)
        {
            return;
        }

        _state = _temporaryItemCount == 0 ? OverlayState.Hidden : OverlayState.Compact;
        _expandedDropActive = false;
        Publish();
    }

    private OverlayState ResolveResumeState()
    {
        if (_resumeAfterModeTransition is OverlayState.DragApproaching or OverlayState.DragReady)
        {
            return _resumeAfterModeTransition;
        }

        if (_temporaryItemCount == 0)
        {
            return OverlayState.Hidden;
        }

        return _resumeAfterModeTransition == OverlayState.Expanded
            ? OverlayState.Expanded
            : OverlayState.Compact;
    }

    private void Publish()
    {
        _revision++;
        Changed?.Invoke(this, Snapshot);
    }
}
