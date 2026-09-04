using DropSpace.Core.Overlay;

namespace DropSpace.App.Services;

/// <summary>
/// Owns native region identity and deduplication. A compositor-only frame never reaches
/// SetWindowRgn unless its physical geometry actually changed.
/// </summary>
internal sealed class OverlayNativeRegionController
{
    private readonly nint _windowHandle;
    private readonly double _scale;
    private readonly OverlayRegionUpdatePolicy _policy = new();

    public OverlayNativeRegionController(nint windowHandle, double scale)
    {
        _windowHandle = windowHandle;
        _scale = scale;
    }

    public long FailureCount { get; private set; }

    public long SkippedUpdates => _policy.SkippedUpdates;

    public OverlayRegionSignature? LastApplied => _policy.LastApplied;

    public bool Apply(
        int left,
        int top,
        int width,
        int height,
        double topRadiusDip,
        double bottomRadiusDip,
        out OverlayNativeFailure? failure)
    {
        var signature = OverlayRegionSignature.Create(
            left,
            top,
            width / _scale,
            height / _scale,
            topRadiusDip,
            bottomRadiusDip,
            _scale);
        if (!_policy.ShouldApply(signature))
        {
            failure = null;
            return true;
        }

        if (signature.IsEmpty)
        {
            failure = new OverlayNativeFailure("Validate non-empty overlay HRGN geometry", true, 87);
            FailureCount++;
            _policy.Reset();
            return false;
        }

        if (!OverlayWindowInterop.ApplyVisualRegion(
                _windowHandle,
                left,
                top,
                width,
                height,
                signature.TopRadiusPixels,
                signature.BottomRadiusPixels,
                out failure))
        {
            FailureCount++;
            _policy.Reset();
            return false;
        }

        return true;
    }

    public bool ApplyEmpty(out OverlayNativeFailure? failure)
    {
        if (!_policy.ShouldApply(OverlayRegionSignature.Empty))
        {
            failure = null;
            return true;
        }

        if (OverlayWindowInterop.ApplyEmptyRegion(_windowHandle, out failure))
        {
            return true;
        }

        FailureCount++;
        _policy.Reset();
        return false;
    }

    public void Reset() => _policy.Reset();
}
