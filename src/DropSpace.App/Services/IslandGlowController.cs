using System.ComponentModel;
using System.Diagnostics;
using DropSpace.Core.Lyrics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services;

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
    private double _phase;
    private long _lastTick;
    private bool _eligible;
    private bool _reducedMotion;
    private bool _disposed;
    private bool _failed;

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
        _surfaceOpacity = Math.Clamp(opacity, 0, 1);
        if (changed && _envelope.Brightness > 0) Render();
    }

    public void RefreshPosition()
    {
        if (IsAvailable && _envelope.Brightness > 0) Render();
    }

    public void SetTarget(bool eligible, double normalizedEnergy, bool reducedMotion)
    {
        if (!IsAvailable) return;
        _eligible = eligible;
        _energy = normalizedEnergy;
        _reducedMotion = reducedMotion;
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
        // Phase survives pause, mode changes and interruptions. Reduced motion freezes
        // the ribbons, but still allows the gentle audio-driven brightness envelope.
        if (!_reducedMotion) _phase += Math.Min(elapsed.TotalSeconds, 0.1);
        _envelope.Advance(_eligible, _energy, elapsed);
        Render();
        if (!_eligible && _envelope.Brightness == 0) _timer.Stop();
    }

    private void Render()
    {
        if (!IsAvailable) return;
        if (_envelope.Brightness == 0 || _surfaceOpacity <= 0.001 || _shape.Width <= 0 || _shape.Height <= 0)
        {
            _window.Hide();
            return;
        }
        try
        {
            _rasterizer ??= new IslandGlowRasterizer(_shape.Width, _shape.Height,
                _shape.TopRadius, _shape.BottomRadius, _scale);
            _rasterizer.Render(_phase, _envelope.Brightness * _surfaceOpacity);
            _window.Present(_rasterizer, _left, _top);
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
        _eligible = false;
        _timer.Stop();
        _envelope.Advance(false, 0, TimeSpan.FromSeconds(5));
        _window.Hide();
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
