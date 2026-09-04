using DropSpace.Core.Overlay;

namespace DropSpace.App.Services;

/// <summary>
/// Couples the interruptible semantic controller to compositor-owned visual channels while
/// retaining a single generation of motion work for the window's active-frame gate.
/// </summary>
internal sealed class OverlayMotionOrchestrator : IDisposable
{
    private readonly OverlayCompositionAnimator _composition;
    private bool _disposed;
    private bool _hasTarget;
    private bool _lastReducedMotion;

    public OverlayMotionOrchestrator(
        OverlayMotionValues initial,
        OverlayCompositionAnimator composition)
    {
        Controller = new OverlayMotionController(initial);
        _composition = composition;
        _composition.SnapTo(initial);
    }

    public OverlayMotionController Controller { get; }

    public OverlayMotionValues Current => Controller.Current;

    public OverlayMotionValues Target => Controller.Target;

    public bool IsAnimating => Controller.IsAnimating;

    public long Generation { get; private set; }

    public void SetTarget(OverlayMotionValues target, bool reducedMotion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hasTarget &&
            Controller.Target == target &&
            _lastReducedMotion == reducedMotion)
        {
            return;
        }

        _hasTarget = true;
        _lastReducedMotion = reducedMotion;
        Generation++;
        _composition.AnimateTo(
            Controller.Current,
            target,
            Controller.Profiles.ContentTransition,
            reducedMotion);
        Controller.SetTarget(target, reducedMotion);
    }

    public bool Step(TimeSpan elapsed) => Controller.Step(elapsed);

    public void SnapTo(OverlayMotionValues values)
    {
        _hasTarget = true;
        _lastReducedMotion = false;
        Generation++;
        Controller.SnapTo(values);
        _composition.SnapTo(values);
    }

    public void PulseDropTarget(double scale) => Controller.PulseDropTarget(scale);

    public void ApplyHover(bool entered, bool reducedMotion) => _composition.ApplyHover(entered, reducedMotion);

    public void ApplyPress(bool pressed, bool reducedMotion) => _composition.ApplyPress(pressed, reducedMotion);

    public void CompleteVisualAnimations() => _composition.SnapTo(Controller.Target);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _composition.Dispose();
    }
}
