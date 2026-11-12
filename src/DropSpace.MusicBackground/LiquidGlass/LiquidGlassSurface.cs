using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using Vortice.Direct3D11;

namespace DropSpace.Rendering;

public readonly record struct GlassOptions(float Blur, float Refraction, float Dispersion, float DarkOverlay, bool BlackGradient, float RefractionWidth = 100);
public readonly record struct GlassFrame(int Left, int Top, int Width, int Height, int TopRadius, int BottomRadius, float Dpi, float ExpandedProgress = 0);

/// <summary>A background-only surface. Capture is memory-only and stops whenever the island is hidden.</summary>
public sealed class LiquidGlassSurface : Grid, IDisposable
{
    private readonly nint _window;
    private readonly SwapChainPanel _panel = new() { IsHitTestVisible = false };
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _uiQueue;
    private LiquidGlassRenderer? _renderer;
    private IDirect3DDevice? _captureDevice;
    private GraphicsCaptureItem? _item;
    private LatestGlassCapture? _capture;
    private TypedEventHandler<GraphicsCaptureItem, object>? _captureClosed;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _lastFrame;
    private Direct3D11CaptureFrame? _displayedCapture;
    private CaptureRateControl? _captureRateControl;
    private double _displayRefreshRate = 60;
    private nint _monitor;
    private CaptureInterop.Rect _monitorRect;
    private Windows.Graphics.SizeInt32 _captureSize;
    private GlassFrame _frame;
    private GlassOptions _options;
    private int _generation;
    private bool _wanted, _starting, _disposed, _excluded, _dirty;
    private bool _diagnosticFrozen;
    private bool _hasRendered;
    private bool _waitingForPanelLayout;
    private bool _hostMovePending;
    private CaptureInterop.Point _renderedOrigin;
    private double _renderedPanelWidth, _renderedPanelHeight;
    private GlassRefreshMeasurement? _measurement;
    private uint _priorAffinity;
    public bool IsReady { get; private set; }
    public string? Failure { get; private set; }
    public long PresentedFrames => _renderer?.PresentedFrames ?? 0;
    public double TargetRefreshRate { get; private set; } = 30;
    public bool HasCaptureRateControl => _captureRateControl?.IsSupported == true;
    public event EventHandler? StateChanged;

    public LiquidGlassSurface(nint window)
    {
        _window = window; IsHitTestVisible = false;
        Children.Add(_panel);
        _uiQueue = DispatcherQueue.GetForCurrentThread();
        _timer = _uiQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += OnMonitorTick;
        _panel.SizeChanged += OnPanelSizeChanged;
        Loaded += OnLoaded; Unloaded += OnUnloaded;
    }

    public void SetState(bool enabled, GlassOptions options)
    {
        if (_disposed) return;
        var optionsChanged = _options != options;
        _dirty |= optionsChanged; _options = options;
        if (_wanted != enabled) { _wanted = enabled; Failure = null; }
        if (!enabled) { Stop(); return; }
        EnsureStarted();
        if (optionsChanged && IsReady) Render();
    }
    public void SetRefreshRate(double rate, double displayRefreshRate)
    {
        if (_disposed) return;
        if (!double.IsFinite(rate) || rate <= 0) rate = 60;
        if (!double.IsFinite(displayRefreshRate) || displayRefreshRate <= 0) displayRefreshRate = 60;
        if (TargetRefreshRate == rate && _displayRefreshRate == displayRefreshRate) return;
        TargetRefreshRate = rate;
        _displayRefreshRate = displayRefreshRate;
        try { ConfigureCaptureRate(); }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail(error); }
    }
    private void ConfigureCaptureRate()
    {
        // At or above native cadence every fresh frame is useful; the latest-slot
        // coalescer still bounds UI work without an additional phase gate.
        _capture?.SetNotificationRate(TargetRefreshRate >= _displayRefreshRate ? double.PositiveInfinity : TargetRefreshRate);
        // Capture at a display-aligned cadence at least as fast as the target.
        // A small interval margin avoids dropping a vblank because of clock jitter.
        // Notification pacing, not this margin, controls material update frequency.
        var captureHz = _displayRefreshRate / Math.Max(1, Math.Floor(_displayRefreshRate / TargetRefreshRate));
        // At native cadence let WGC follow the compositor directly. A nonzero
        // minimum can otherwise skip alternating vblanks on high-Hz displays.
        _captureRateControl?.SetRate(TargetRefreshRate >= _displayRefreshRate ? 0 : captureHz / .9);
    }
    public void SetGeometry(GlassFrame frame)
    {
        if (_disposed || frame.Width <= 0 || frame.Height <= 0) return;
        _dirty |= _frame != frame; _frame = frame;
        if (_wanted) { EnsureStarted(); Render(); }
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // A static desktop can deliver its only frame before XAML has arranged the panel.
        // Ordinary resize animation publishes the matching shape through SetGeometry.
        if (_waitingForPanelLayout && args.NewSize.Width > 0 && args.NewSize.Height > 0) Render();
    }

    public void OnHostMoved()
    {
        if (!_wanted || !IsReady) return;
        if (CaptureInterop.MonitorFromWindow(_window, 2) != _monitor)
        {
            Stop(); EnsureStarted(); return;
        }
        // WGC need not deliver a new desktop image when the excluded HWND moves.
        // Several input events before one display frame need only the final origin.
        if (_hostMovePending) return;
        _hostMovePending = true;
        CompositionTarget.Rendering += OnHostMoveFrame;
    }

    private void OnHostMoveFrame(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnHostMoveFrame;
        _hostMovePending = false;
        Render();
    }
    private void OnLoaded(object sender, RoutedEventArgs args) => EnsureStarted();
    private void OnUnloaded(object sender, RoutedEventArgs args) => Stop();

    private async void EnsureStarted()
    {
        if (!_wanted || _disposed || !IsLoaded || Failure is not null || _starting || _diagnosticFrozen || _session is not null || _frame.Width <= 0) return;
        _starting = true;
        var generation = ++_generation;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is unavailable.");
            var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            if (_disposed || generation != _generation || !_wanted) return;
            if (access != AppCapabilityAccessStatus.Allowed)
                throw new NotSupportedException("Borderless background capture is unavailable; using the existing material.");
            _monitor = CaptureInterop.MonitorFromWindow(_window, 2);
            var info = new CaptureInterop.MonitorInfo { Size = Marshal.SizeOf<CaptureInterop.MonitorInfo>() };
            if (!CaptureInterop.GetMonitorInfoW(_monitor, ref info)) throw new InvalidOperationException("Cannot resolve the island monitor.");
            _monitorRect = info.Monitor;
            _renderer = new LiquidGlassRenderer(_panel);
            _renderer.Measurement = _measurement;
            _captureDevice = CaptureInterop.CreateCaptureDevice(_renderer.Device);
            _item = CaptureInterop.CreateMonitorItem(_monitor);
            _captureClosed = (_, _) => _uiQueue.TryEnqueue(() =>
            {
                if (generation == _generation) Fail(new InvalidOperationException("Background capture ended."));
            });
            _item.Closed += _captureClosed;
            _captureSize = _item.Size;
            _capture = new LatestGlassCapture(_captureDevice, _captureSize, CreateFrameNotification(generation));
            _capture.SetMeasurement(_measurement);
            _session = _capture.CreateSession(_item);
            _session.IsCursorCaptureEnabled = false;
            _session.IsBorderRequired = false;
            _captureRateControl = new CaptureRateControl(_session);
            ConfigureCaptureRate();
            if (!CaptureInterop.GetWindowDisplayAffinity(_window, out _priorAffinity) ||
                !CaptureInterop.SetWindowDisplayAffinity(_window, 0x11))
                throw new InvalidOperationException("Cannot exclude the island from its own background capture.");
            _excluded = true;
            _session.StartCapture();
            _timer.Start();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (generation == _generation) Fail(error); }
        finally { if (generation == _generation) _starting = false; }
    }

    private Action CreateFrameNotification(int generation)
    {
        // An outgoing pool's queued callback cannot suppress the new pool's first frame.
        int queued = 0;
        void Consume()
        {
            Interlocked.Exchange(ref queued, 0);
            if (generation == _generation && !_diagnosticFrozen) ConsumeFrame();
        }
        return () =>
        {
            if (Interlocked.Exchange(ref queued, 1) == 0 && !_uiQueue.TryEnqueue(Consume))
                Interlocked.Exchange(ref queued, 0);
        };
    }

    private void OnMonitorTick(DispatcherQueueTimer sender, object args)
    {
        if (_wanted && !_disposed && CaptureInterop.MonitorFromWindow(_window, 2) != _monitor)
        {
            Stop(); EnsureStarted();
        }
    }

    private void ConsumeFrame()
    {
        if (!_wanted || _disposed) return;
        _measurement?.Tick();
        try
        {
            if (CaptureInterop.MonitorFromWindow(_window, 2) != _monitor) { Stop(); EnsureStarted(); return; }
            var resized = false;
            long receivedAt = 0;
            var capture = _capture?.TakeLatest(out receivedAt);
            try
            {
                if (capture is not null)
                {
                    _measurement?.Capture(capture.SystemRelativeTime, receivedAt);
                    resized = capture.ContentSize.Width != _captureSize.Width || capture.ContentSize.Height != _captureSize.Height;
                    if (!resized)
                    {
                        var texture = CaptureInterop.GetTexture(capture.Surface);
                        _lastFrame?.Dispose(); _displayedCapture?.Dispose();
                        _lastFrame = texture; _displayedCapture = capture; capture = null;
                        _dirty = true;
                    }
                }
                else _measurement?.EmptyPoll();
            }
            finally { capture?.Dispose(); }
            // The retained lease is not copied in full. Only the padded island
            // region is copied by Render; pending/producer buffers remain available.
            if (resized) { Stop(); EnsureStarted(); return; }
            if (_dirty) Render();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail(error); }
    }

    private void Render()
    {
        if (_lastFrame is null || _renderer is null) return;
        _waitingForPanelLayout = _panel.ActualWidth <= 0 || _panel.ActualHeight <= 0;
        if (_waitingForPanelLayout) return;
        try
        {
            var measuredAt = _measurement is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            var origin = new CaptureInterop.Point();
            if (!CaptureInterop.ClientToScreen(_window, ref origin)) throw new InvalidOperationException("Cannot locate the island background.");
            if (!_dirty && _hasRendered && origin.X == _renderedOrigin.X && origin.Y == _renderedOrigin.Y &&
                _panel.ActualWidth == _renderedPanelWidth && _panel.ActualHeight == _renderedPanelHeight)
            {
                _measurement?.SkippedRender();
                return;
            }
            _renderer.Render(_lastFrame, origin.X + _frame.Left - _monitorRect.Left, origin.Y + _frame.Top - _monitorRect.Top,
                _frame, _options, (float)(_frame.Width / _panel.ActualWidth), (float)(_frame.Height / _panel.ActualHeight));
            if (measuredAt != 0) _measurement?.Render(measuredAt);
            _hasRendered = true; _renderedOrigin = origin;
            _renderedPanelWidth = _panel.ActualWidth; _renderedPanelHeight = _panel.ActualHeight;
            _dirty = false;
            if (!IsReady) { IsReady = true; StateChanged?.Invoke(this, EventArgs.Empty); }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail(error); }
    }
    public void BeginDiagnosticMeasurement()
    {
        _measurement = new();
        _capture?.SetMeasurement(_measurement);
        if (_renderer is not null) _renderer.Measurement = _measurement;
    }
    public object CompareDiagnosticSampling(bool compareWidth = false)
    {
        if (_lastFrame is null || _renderer is null || !IsReady) throw new InvalidOperationException("Glass is not ready.");
        var origin = new CaptureInterop.Point();
        if (!CaptureInterop.ClientToScreen(_window, ref origin)) throw new InvalidOperationException("Cannot locate the island.");
        return _renderer.CompareSampling(_lastFrame, origin.X + _frame.Left - _monitorRect.Left,
            origin.Y + _frame.Top - _monitorRect.Top, _frame, _options,
            (float)(_frame.Width / _panel.ActualWidth), (float)(_frame.Height / _panel.ActualHeight), compareWidth);
    }
    public object? EndDiagnosticMeasurement()
    {
        _capture?.SetMeasurement(null);
        var result = _measurement?.Complete(_captureSize.Width, _captureSize.Height, _frame, TargetRefreshRate,
            _renderer?.DiagnosticSwapChainAddress ?? 0);
        _measurement = null;
        if (_renderer is not null) _renderer.Measurement = null;
        return result;
    }
    // Explicit diagnostic only: stop sampling before restoring screenshot visibility,
    // so a native UI screenshot can show the rendered material without self-feedback.
    public void FreezeForDiagnosticCapture()
    {
        _diagnosticFrozen = true;
        if (_lastFrame is not null && _displayedCapture is not null && _renderer is not null)
        {
            // Explicit screenshot diagnostics outlive the frame pool. Production
            // rendering never allocates or copies this full-screen snapshot.
            var description = _lastFrame.Description;
            description.BindFlags = BindFlags.None; description.MiscFlags = ResourceOptionFlags.None;
            var snapshot = _renderer.Device.CreateTexture2D(description);
            _renderer.CopyFrame(snapshot, _lastFrame);
            _lastFrame.Dispose(); _lastFrame = snapshot;
            _displayedCapture.Dispose(); _displayedCapture = null;
        }
        _captureRateControl?.Dispose(); _captureRateControl = null;
        _timer.Stop(); _session?.Dispose(); _session = null;
        _capture?.Dispose(); _capture = null;
        if (_excluded && CaptureInterop.SetWindowDisplayAffinity(_window, _priorAffinity)) _excluded = false;
    }
    private void Fail(Exception error) { Failure = error.ToString(); Stop(); StateChanged?.Invoke(this, EventArgs.Empty); }
    private void Stop()
    {
        if (_hostMovePending)
        {
            CompositionTarget.Rendering -= OnHostMoveFrame;
            _hostMovePending = false;
        }
        ++_generation; _starting = false; _diagnosticFrozen = false; _timer.Stop();
        if (_item is not null && _captureClosed is not null) _item.Closed -= _captureClosed;
        _captureClosed = null;
        _lastFrame?.Dispose(); _lastFrame = null;
        _displayedCapture?.Dispose(); _displayedCapture = null;
        _captureRateControl?.Dispose(); _captureRateControl = null;
        _session?.Dispose(); _session = null; _capture?.Dispose(); _capture = null; _item = null;
        _hasRendered = false; _waitingForPanelLayout = false;
        _captureDevice?.Dispose(); _captureDevice = null;
        _renderer?.Dispose(); _renderer = null;
        if (_excluded)
        {
            if (!CaptureInterop.SetWindowDisplayAffinity(_window, _priorAffinity))
                System.Diagnostics.Trace.TraceError("Could not restore island capture visibility: {0}", Marshal.GetLastWin32Error());
            else _excluded = false;
        }
        if (IsReady) { IsReady = false; StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _wanted = false;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; _timer.Tick -= OnMonitorTick;
        _panel.SizeChanged -= OnPanelSizeChanged;
        Stop(); Children.Clear();
    }
}
