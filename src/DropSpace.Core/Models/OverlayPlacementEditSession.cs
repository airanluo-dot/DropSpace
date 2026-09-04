using DropSpace.Core.DragDrop;

namespace DropSpace.Core.Models;

public enum OverlayPlacementEditState
{
    Inactive,
    Armed,
    Dragging,
}

/// <summary>
/// Holds the transient state of a direct Dynamic Island placement edit. It never persists data;
/// the caller commits the returned preview through the normal settings transaction.
/// </summary>
public sealed class OverlayPlacementEditSession
{
    private OverlayCustomPlacement _original = new(0, 0);
    private OverlayCustomPlacement _preview = new(0, 0);
    private OverlayCustomPlacement _dragOrigin = new(0, 0);
    private DragScreenPoint _pointerStart;

    public OverlayPlacementEditState State { get; private set; }

    public OverlayCustomPlacement Preview => _preview;

    public void Arm(OverlayCustomPlacement savedOriginal, OverlayCustomPlacement projectedStart)
    {
        ValidatePlacement(savedOriginal);
        ValidatePlacement(projectedStart);
        _original = savedOriginal;
        _dragOrigin = projectedStart;
        _preview = projectedStart;
        _pointerStart = default;
        State = OverlayPlacementEditState.Armed;
    }

    public bool TryBeginDrag(
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Pointer is the established input parameter name in this public gesture contract.")]
        DragScreenPoint pointer)
    {
        if (State != OverlayPlacementEditState.Armed)
        {
            return false;
        }

        _pointerStart = pointer;
        State = OverlayPlacementEditState.Dragging;
        return true;
    }

    public OverlayCustomPlacement Move(
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Pointer is the established input parameter name in this public gesture contract.")]
        DragScreenPoint pointer,
        double monitorScale)
    {
        if (State != OverlayPlacementEditState.Dragging)
        {
            return _preview;
        }

        if (!double.IsFinite(monitorScale) || monitorScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorScale));
        }

        _preview = new OverlayCustomPlacement(
            _dragOrigin.X + (pointer.X - _pointerStart.X) / monitorScale,
            _dragOrigin.Y + (pointer.Y - _pointerStart.Y) / monitorScale);
        return _preview;
    }

    public OverlayCustomPlacement Commit()
    {
        if (State == OverlayPlacementEditState.Inactive)
        {
            return _preview;
        }

        var committed = _preview;
        State = OverlayPlacementEditState.Inactive;
        return committed;
    }

    public OverlayCustomPlacement Cancel()
    {
        var restored = _original;
        _preview = restored;
        State = OverlayPlacementEditState.Inactive;
        return restored;
    }

    private static void ValidatePlacement(OverlayCustomPlacement placement)
    {
        if (!double.IsFinite(placement.X) || !double.IsFinite(placement.Y) ||
            Math.Abs(placement.X) > 100_000 || Math.Abs(placement.Y) > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }
    }
}
