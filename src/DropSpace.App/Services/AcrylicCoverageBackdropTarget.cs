using DropSpace.Core.Overlay;
using Microsoft.UI.Composition;
using System.Runtime.InteropServices;
using Wuc = Windows.UI.Composition;

namespace DropSpace.App.Services;

/// <summary>Intercepts every controller brush assignment while preserving its target identity.</summary>
internal sealed partial class AcrylicCoverageBackdropTarget : ICompositionSupportsSystemBackdrop, IDisposable
{
    private readonly ICompositionSupportsSystemBackdrop _target;
    private Wuc.CompositionBrush? _source;
    private AcrylicCoverageMask? _mask;
    private AcrylicShutterBlur? _blur;
    private bool _blurFaulted;
    private string? _blurError;
    private OverlayRegionSignature? _geometry;
    private bool _disposed;
    private GCHandle _failedDetachRoot;

    internal AcrylicCoverageBackdropTarget(ICompositionSupportsSystemBackdrop target) => _target = target;

    public Wuc.CompositionBrush? SystemBackdrop
    {
        get => _source;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (value is null)
            {
                // Detach the native target before dropping a brush still used by composition.
                _target.SystemBackdrop = null;
                var blur = _blur; _blur = null;
                try { blur?.ReleaseAfterTargetDetached(); }
                finally { _mask?.SetSource(null); _source = null; }
                return;
            }
            if (_geometry is not { IsEmpty: false } geometry)
            {
                _target.SystemBackdrop = value;
                _source = value;
                return;
            }
            ApplySource(value, geometry);
        }
    }

    internal void UpdateGeometry(OverlayRegionSignature geometry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (geometry.IsEmpty) return;
        if (_mask is not null)
            _mask.UpdateGeometry(geometry.WidthPixels, geometry.HeightPixels, geometry.TopRadiusPixels, geometry.BottomRadiusPixels);
        else if (_source is not null) ApplySource(_source, geometry);
        _geometry = geometry;
    }

    private void ApplySource(Wuc.CompositionBrush source, OverlayRegionSignature geometry)
    {
        var prior = _mask;
        var mask = prior;
        if (mask is null || !ReferenceEquals(mask.Compositor, source.Compositor))
            mask = new AcrylicCoverageMask(source.Compositor);
        try
        {
            mask.UpdateGeometry(geometry.WidthPixels, geometry.HeightPixels, geometry.TopRadiusPixels, geometry.BottomRadiusPixels);
            if (ReferenceEquals(prior, mask) && _blur is not null) _blur.SetSource(source);
            else mask.SetSource(source);
            _target.SystemBackdrop = mask.Brush;
        }
        catch
        {
            if (!ReferenceEquals(prior, mask)) mask.Dispose();
            else if (_blur is not null) _blur.SetSource(_source, force: true);
            else mask.SetSource(_source);
            throw;
        }
        _mask = mask;
        _source = source;
        if (prior is not null && !ReferenceEquals(prior, mask))
        {
            var blur = _blur; _blur = null; _blurFaulted = false; _blurError = null;
            try { blur?.ReleaseAfterTargetDetached(); }
            finally { prior.Dispose(); }
        }
    }

    // Call after this frame's physical mask geometry is committed. Never reconnects the
    // Acrylic controller or reassigns the real target during the frame loop.
    internal void PrepareMotion() => SetMotion(IslandMotionBlurFrame.None, prepare: true);
    internal void SetMotion(IslandMotionBlurFrame frame) => SetMotion(frame, prepare: false);

    private void SetMotion(IslandMotionBlurFrame frame, bool prepare)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mask is null || _source is null || _blurFaulted) return;
        if (_blur is null && !frame.IsActive && !prepare) return;
        try
        {
            _blur ??= new AcrylicShutterBlur(_mask);
            _blur.SetSource(_source);
            _blur.SetMotion(frame);
        }
        catch (Exception error)
        {
            _blurFaulted = true; _blurError = error.ToString();
            // Original coverage + Acrylic remains the fallback. Restoration must succeed
            // before owned effects can be released; failed restoration retains their root.
            _mask.SetSource(_source);
            var blur = _blur; _blur = null;
            try { blur?.ReleaseAfterTargetDetached(); }
            catch (Exception cleanupError) { System.Diagnostics.Trace.TraceError("Motion effect cleanup failed: {0}", cleanupError); }
        }
    }

    internal object CaptureMotionState() => new { faulted = _blurFaulted, error = _blurError, graph = _blur?.Snapshot() };

    internal object? Snapshot() => _mask?.Snapshot();
    internal byte[] Coverage => _mask?.Coverage ?? [];

    public void Dispose()
    {
        if (_disposed) return;
        // A failed target clear leaves the owned graph rooted and available for a retry.
        try { _target.SystemBackdrop = null; }
        catch
        {
            // A native target can still reference this graph. Keep it alive until the
            // owning XAML thread closes that target, rather than finalizing live brushes.
            if (!_failedDetachRoot.IsAllocated) _failedDetachRoot = GCHandle.Alloc(this);
            throw;
        }
        ReleaseAfterTargetClosed();
    }

    internal void ReleaseAfterTargetClosed()
    {
        if (_disposed) return;
        _disposed = true;
        if (_failedDetachRoot.IsAllocated) _failedDetachRoot.Free();
        _source = null;
        var blur = _blur; _blur = null;
        var mask = _mask; _mask = null;
        try { blur?.ReleaseAfterTargetDetached(); }
        finally { mask?.Dispose(); }
    }
}
