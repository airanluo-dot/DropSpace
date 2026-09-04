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
}

public enum OverlayTransitionCause
{
    Restore,
    ItemCountChanged,
    DragApproach,
    DragReady,
    DropTargetEntered,
    DragCancelled,
    DropCompleted,
    VisibleDropCompleted,
    Expanded,
    QuickPanelOpened,
    Collapsed,
    Dismissed,
    MotionPreferenceChanged,
    VisualPreferenceChanged,
    FullscreenSuppressed,
    MonitorChanged,
}

public sealed record OverlayTransitionDescriptor(
    OverlayState From,
    OverlayState To,
    OverlayTransitionCause Cause,
    OverlayMotionPreference MotionPreference);

public sealed record OverlaySnapshot(
    OverlayState State,
    int TemporaryItemCount,
    bool ExpandedDropActive,
    long Revision,
    OverlayTransitionDescriptor? Transition = null);

public sealed class OverlayStateMachine
{
    private OverlayState _state = OverlayState.Hidden;
    private int _temporaryItemCount;
    private bool _expandedDropActive;
    private long _revision;
    private OverlayMotionPreference _motionPreference = OverlayMotionPreference.System;
    private OverlayTransitionDescriptor? _transition;
    private OverlayState _lastPublishedState = OverlayState.Hidden;

    public event EventHandler<OverlaySnapshot>? Changed;

    public OverlaySnapshot Snapshot => new(
        _state,
        _temporaryItemCount,
        _expandedDropActive,
        _revision,
        _transition);

    public bool SetMotionPreference(OverlayMotionPreference preference)
    {
        if (_motionPreference == preference)
        {
            return false;
        }

        _motionPreference = preference;
        Publish(OverlayTransitionCause.MotionPreferenceChanged);
        return true;
    }

    public void Restore(int temporaryItemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryItemCount);
        _temporaryItemCount = temporaryItemCount;
        _expandedDropActive = false;
        _state = temporaryItemCount == 0 ? OverlayState.Hidden : OverlayState.Compact;
        Publish(OverlayTransitionCause.Restore);
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

        Publish(OverlayTransitionCause.ItemCountChanged);
    }

    public void BeginDragApproach()
    {
        _state = OverlayState.DragApproaching;
        _expandedDropActive = false;
        Publish(OverlayTransitionCause.DragApproach);
    }

    public void BeginVisibleDrag()
    {
        if (_state == OverlayState.Expanded)
        {
            _expandedDropActive = true;
            Publish(OverlayTransitionCause.DropTargetEntered);
            return;
        }

        BeginDragApproach();
    }

    public void SetDragReady(bool ready)
    {
        if (_expandedDropActive)
        {
            Publish(OverlayTransitionCause.DropTargetEntered);
            return;
        }

        if (_state is not (OverlayState.DragApproaching or OverlayState.DragReady))
        {
            return;
        }

        _state = ready ? OverlayState.DragReady : OverlayState.DragApproaching;
        Publish(OverlayTransitionCause.DragReady);
    }

    public void CancelDrag()
    {
        if (_expandedDropActive)
        {
            _expandedDropActive = false;
            Publish(OverlayTransitionCause.DragCancelled);
            return;
        }

        if (_state is not (OverlayState.DragApproaching or OverlayState.DragReady))
        {
            return;
        }

        _state = _temporaryItemCount == 0 ? OverlayState.Dismissing : OverlayState.Compact;
        Publish(OverlayTransitionCause.DragCancelled);
    }

    public void CompleteDrop(int temporaryItemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(temporaryItemCount);
        _temporaryItemCount = temporaryItemCount;
        _expandedDropActive = false;
        _state = temporaryItemCount == 0 ? OverlayState.Dismissing : OverlayState.Compact;
        Publish(OverlayTransitionCause.DropCompleted);
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
        Publish(OverlayTransitionCause.VisibleDropCompleted);
    }

    public void Expand()
    {
        if (_temporaryItemCount > 0 && _state == OverlayState.Compact)
        {
            _state = OverlayState.Expanded;
            Publish(OverlayTransitionCause.Expanded);
        }
    }

    public void OpenQuickPanel()
    {
        _expandedDropActive = false;
        _state = OverlayState.Expanded;
        Publish(OverlayTransitionCause.QuickPanelOpened);
    }

    public void Collapse()
    {
        if (_state != OverlayState.Expanded)
        {
            return;
        }

        _state = _temporaryItemCount == 0 ? OverlayState.Dismissing : OverlayState.Compact;
        _expandedDropActive = false;
        Publish(OverlayTransitionCause.Collapsed);
    }

    public void CompleteDismissal()
    {
        if (_state != OverlayState.Dismissing)
        {
            return;
        }

        _state = _temporaryItemCount == 0 ? OverlayState.Hidden : OverlayState.Compact;
        _expandedDropActive = false;
        Publish(OverlayTransitionCause.Dismissed);
    }

    private void Publish(OverlayTransitionCause cause)
    {
        var from = _lastPublishedState;
        _transition = new OverlayTransitionDescriptor(from, _state, cause, _motionPreference);
        _lastPublishedState = _state;
        _revision++;
        Changed?.Invoke(this, Snapshot);
    }
}
