using System.Diagnostics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace DropSpace.Rendering;

/// <summary>
/// Receives frames independently of the UI dispatcher, retaining at most one pending
/// frame lease. Replacement immediately returns the old buffer to WGC. No XAML or
/// D3D immediate-context work runs on the capture callback thread.
/// </summary>
internal sealed class LatestGlassCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly Direct3D11CaptureFramePool _pool;
    private Direct3D11CaptureFrame? _pending;
    private long _receivedAt;
    private Exception? _failure;
    private GlassRefreshMeasurement? _measurement;
    private bool _disposed;
    private readonly Action _frameAvailable;
    private double _notificationPeriod, _nextNotification;
    private bool _notificationsEnabled;

    internal LatestGlassCapture(IDirect3DDevice device, SizeInt32 size, Action frameAvailable)
    {
        _frameAvailable = frameAvailable;
        // One displayed lease, one latest pending lease and one producer buffer.
        // The displayed lease replaces our previous full-screen GPU copy, not a queue.
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
        _pool.FrameArrived += OnFrameArrived;
    }

    internal GraphicsCaptureSession CreateSession(GraphicsCaptureItem item) => _pool.CreateCaptureSession(item);

    internal void SetMeasurement(GlassRefreshMeasurement? measurement)
    {
        lock (_gate) _measurement = measurement;
    }
    internal void SetNotificationRate(double rate)
    {
        lock (_gate)
        {
            _notificationsEnabled = rate > 0;
            _notificationPeriod = rate > 0 ? Stopwatch.Frequency / rate : 0;
            _nextNotification = 0;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var notify = false;
        lock (_gate)
        {
            if (_disposed || _failure is not null) return;
            try
            {
                // Drain queued frames, then keep only the newest lease. Never enqueue
                // one UI callback per frame: a busy UI must not build a stale backlog.
                while (sender.TryGetNextFrame() is { } frame)
                {
                    var previous = _pending;
                    _pending = frame;
                    _receivedAt = Stopwatch.GetTimestamp();
                    previous?.Dispose();
                    _measurement?.Arrived(frame.SystemRelativeTime, _receivedAt, previous is not null);
                }
                if (_notificationsEnabled)
                {
                    // Pace by the compositor timestamp, not callback-delivery jitter.
                    var now = _pending is { } latest
                        ? latest.SystemRelativeTime.TotalSeconds * Stopwatch.Frequency : Stopwatch.GetTimestamp();
                    // Phase-based pacing handles displays whose rate isn't divisible
                    // by the selected target. Missed deadlines never queue catch-up work.
                    var deadline = now + _notificationPeriod * .1;
                    notify = _notificationPeriod == 0 || _nextNotification == 0 || deadline >= _nextNotification;
                    if (notify && _notificationPeriod > 0) _nextNotification = _nextNotification == 0 ? now + _notificationPeriod
                        : _nextNotification + Math.Max(1, Math.Floor((deadline - _nextNotification) / _notificationPeriod) + 1) * _notificationPeriod;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { _failure = error; notify = _notificationsEnabled; }
        }
        if (notify) _frameAvailable();
    }

    // Transfers the live lease to the UI owner. The third buffer lets it retain
    // the displayed source for exact geometry redraws while reception continues.
    internal Direct3D11CaptureFrame? TakeLatest(out long receivedAt)
    {
        lock (_gate)
        {
            receivedAt = _receivedAt;
            if (_failure is not null) throw new InvalidOperationException("Background frame reception failed.", _failure);
            var frame = _pending;
            _pending = null;
            return frame;
        }
    }

    public void Dispose()
    {
        Direct3D11CaptureFrame? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pending; _pending = null; _measurement = null;
        }
        // Do not close the pool under the callback lock: closing may wait for a
        // callback that has already started. It now observes _disposed and returns.
        _pool.FrameArrived -= OnFrameArrived;
        pending?.Dispose();
        _pool.Dispose();
    }
}
