using System.ComponentModel;
using System.Diagnostics;
using DropSpace.Core.Lyrics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services;

internal sealed record IslandGlowTransfer(LyricsGlowHandoff State, long Timestamp);

/// <summary>Owns one interruptible envelope and phase for this island's entire lifetime.</summary>
internal sealed class IslandGlowController : IDisposable
{
    private readonly LyricsGlowEnvelope _envelope = new();
    private readonly IslandGlowWindow _window;
    private readonly DispatcherQueueTimer _timer;
    private readonly ILogger _logger;
    private readonly double _scale;
    private IslandGlowRasterizer? _rasterizer;
    private (int Width, int Height, int TopRadius, int BottomRadius) _shape;
    private int _left;
    private int _top;
    private double _surfaceOpacity;
    private double _energy;
    private readonly double[] _bands = new double[6];
    private long _lastTick;
    private bool _eligible;
    private bool _reducedMotion;
    private bool _simplifiedGlow;
    private bool _disposed;
    private bool _failed;
    private bool _captureCurrentFrame;
    private bool _captureTargetFresh;
    private bool _presentationDirty = true;

    public IslandGlowController(nint owner, double scale, DispatcherQueue dispatcher, ILogger logger)
    {
        _window = new IslandGlowWindow(owner);
        _scale = scale;
        _logger = logger;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
    }

    public bool IsAvailable => !_disposed && !_failed;
    public LyricsGlowVisualState? CaptureVisualState() => IsAvailable && _captureCurrentFrame && _eligible && _envelope.Brightness > 0
        ? _envelope.Capture() : null;

    // A target change does not yet make the preceding frame belong to that track.
    // Only an actual advancement with the current target can authorize a capture.
    public void InvalidateFrameCapture()
    {
        _captureCurrentFrame = false;
        _captureTargetFresh = false;
    }

    // Called only after current-window policy and layout authorize the continuation.
    // SetTarget below owns timer startup with a fresh monotonic timestamp.
    public void RestoreVisualState(LyricsGlowVisualState state)
    {
        if (IsAvailable) _envelope.Restore(state);
    }

    public void SetGeometry(int left, int top, int width, int height, int topRadius, int bottomRadius, double opacity)
    {
        if (!IsAvailable) return;
        var shape = (width, height, topRadius, bottomRadius);
        var changed = _shape != shape || _left != left || _top != top || Math.Abs(_surfaceOpacity - opacity) > 0.002;
        if (_shape != shape)
        {
            _shape = shape;
            _rasterizer = null; // Allocate only if this shape actually needs to glow.
        }
        _left = left;
        _top = top;
        _surfaceOpacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 0;
        _presentationDirty |= changed;
        if (changed && _envelope.Brightness > 0) Render();
    }

    public void RefreshPosition()
    {
        _presentationDirty = true;
        if (IsAvailable && _envelope.Brightness > 0) Render();
    }

    public void SetTarget(bool eligible, double normalizedEnergy, bool reducedMotion, IReadOnlyList<double>? bands = null,
        bool simplifiedGlow = false)
    {
        if (!IsAvailable) return;
        _eligible = eligible;
        _captureTargetFresh = true;
        _energy = normalizedEnergy;
        _reducedMotion = reducedMotion;
        _simplifiedGlow = simplifiedGlow;
        for (var i = 0; i < _bands.Length; i++)
            _bands[i] = eligible && bands is not null && i < bands.Count && double.IsFinite(bands[i]) ? Math.Clamp(bands[i], 0, 1) : 0;
        if ((_eligible || _envelope.Brightness > 0) && !_timer.IsRunning)
        {
            _lastTick = Stopwatch.GetTimestamp();
            _timer.Start();
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsAvailable) return;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTick, now);
        _lastTick = now;
        AdvanceFrame(elapsed);
    }

    private void AdvanceFrame(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        _envelope.Advance(_eligible, _energy, elapsed, _reducedMotion, _bands, _simplifiedGlow);
        _captureCurrentFrame = _captureTargetFresh;
        Render();
        if (!_eligible && _envelope.Brightness == 0) _timer.Stop();
    }

    internal void VerifyFrameCaptureFenceForSmoke()
    {
        try
        {
            SetTarget(true, .5, false, [.3, .2, .1, .4, .5, .2]);
            AdvanceFrame(TimeSpan.FromMilliseconds(33));
            if (CaptureVisualState() is null) throw new InvalidOperationException("An advanced eligible frame was not capturable.");
            InvalidateFrameCapture();
            AdvanceFrame(TimeSpan.FromMilliseconds(33));
            if (CaptureVisualState() is not null) throw new InvalidOperationException("An old target timer tick reopened the capture fence.");
            SetTarget(true, .7, false, [.1, .5, .3, .2, .4, .8]);
            if (CaptureVisualState() is not null) throw new InvalidOperationException("A new track target relabeled the preceding glow frame.");
            // A→B→A before a frame must remain invalid, even if the final identity matches.
            InvalidateFrameCapture();
            SetTarget(true, .5, false, [.3, .2, .1, .4, .5, .2]);
            if (CaptureVisualState() is not null) throw new InvalidOperationException("A target reversal revived an unadvanced glow frame.");
            AdvanceFrame(TimeSpan.FromMilliseconds(33));
            if (CaptureVisualState() is null) throw new InvalidOperationException("Current input did not reauthorize frame capture.");
        }
        finally { HideImmediately(); }
    }

    private void Render()
    {
        if (!IsAvailable) return;
        if (_envelope.Brightness == 0 || _surfaceOpacity <= 0.001 || _shape.Width <= 0 || _shape.Height <= 0)
        {
            _window.Hide();
            _presentationDirty = true;
            return;
        }
        try
        {
            // Even a cached static frame must observe native hide/destruction.
            // Returning visibility also forces the existing pixels to be shown again.
            if (!_window.CanPresent())
            {
                _presentationDirty = true;
                return;
            }
            _rasterizer ??= new IslandGlowRasterizer(_shape.Width, _shape.Height,
                _shape.TopRadius, _shape.BottomRadius, _scale);
            var changed = _rasterizer.Render(_envelope.Phase, _envelope.Brightness * _surfaceOpacity, _envelope.Bands, _envelope.Simplification);
            if (changed || _presentationDirty)
            {
                _window.Present(_rasterizer, _left, _top);
                _presentationDirty = false;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or OverflowException or ArgumentException)
        {
            // Optional decoration must never take the island down or leave an orphan
            // HWND. Every native operation and cleanup happens on this UI dispatcher.
            _failed = true;
            _timer.Stop();
            _window.Dispose();
            _logger.LogWarning(exception, "Island exterior glow is unavailable; the island remains usable.");
        }
    }

    public void HideImmediately()
    {
        InvalidateFrameCapture();
        _eligible = false;
        _timer.Stop();
        _envelope.Advance(false, 0, TimeSpan.FromSeconds(5));
        _window.Hide();
        _presentationDirty = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        HideImmediately();
        _disposed = true;
        _timer.Tick -= OnTick;
        _window.Dispose();
        _rasterizer = null;
    }
}
