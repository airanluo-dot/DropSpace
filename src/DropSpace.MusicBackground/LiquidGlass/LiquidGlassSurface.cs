using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using Vortice.Direct3D11;

namespace DropSpace.Rendering;

public readonly record struct GlassOptions(float Blur, float Refraction, float Dispersion, float DarkOverlay, bool BlackGradient);
public readonly record struct GlassFrame(int Left, int Top, int Width, int Height, int TopRadius, int BottomRadius, float Dpi);

/// <summary>A background-only surface. Capture is memory-only and stops whenever the island is hidden.</summary>
public sealed class LiquidGlassSurface : Grid, IDisposable
{
    private readonly nint _window;
    private readonly SwapChainPanel _panel = new() { IsHitTestVisible = false };
    private readonly DispatcherQueueTimer _timer;
    private LiquidGlassRenderer? _renderer;
    private IDirect3DDevice? _captureDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _lastFrame;
    private nint _monitor;
    private CaptureInterop.Rect _monitorRect;
    private Windows.Graphics.SizeInt32 _captureSize;
    private GlassFrame _frame;
    private GlassOptions _options;
    private int _generation;
    private bool _wanted, _starting, _disposed, _excluded, _dirty;
    private bool _diagnosticFrozen;
    private uint _priorAffinity;
    public bool IsReady { get; private set; }
    public string? Failure { get; private set; }
    public long PresentedFrames => _renderer?.PresentedFrames ?? 0;
    public event EventHandler? StateChanged;

    public LiquidGlassSurface(nint window)
    {
        _window = window; IsHitTestVisible = false;
        Children.Add(_panel);
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1000d / 30);
        _timer.Tick += OnTick;
        Loaded += OnLoaded; Unloaded += OnUnloaded;
    }

    public void SetState(bool enabled, GlassOptions options)
    {
        if (_disposed) return;
        _dirty |= _options != options; _options = options;
        if (_wanted != enabled) { _wanted = enabled; Failure = null; }
        if (!enabled) Stop(); else EnsureStarted();
    }
    public void SetGeometry(GlassFrame frame)
    {
        if (_disposed || frame.Width <= 0 || frame.Height <= 0) return;
        _dirty |= _frame != frame; _frame = frame;
        if (_wanted) { EnsureStarted(); if (IsReady) Render(); }
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
            _captureDevice = CaptureInterop.CreateCaptureDevice(_renderer.Device);
            _item = CaptureInterop.CreateMonitorItem(_monitor);
            _item.Closed += OnCaptureClosed;
            _captureSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.Create(_captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _captureSize);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            _session.IsBorderRequired = false;
            if (!CaptureInterop.GetWindowDisplayAffinity(_window, out _priorAffinity) ||
                !CaptureInterop.SetWindowDisplayAffinity(_window, 0x11))
                throw new InvalidOperationException("Cannot exclude the island from its own background capture.");
            _excluded = true;
            _session.StartCapture();
            _timer.Start();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail(error); }
        finally { if (generation == _generation) _starting = false; }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!_wanted || _disposed) return;
        try
        {
            if (CaptureInterop.MonitorFromWindow(_window, 2) != _monitor) { Stop(); EnsureStarted(); return; }
            using var capture = _pool?.TryGetNextFrame();
            if (capture is not null)
            {
                if (capture.ContentSize.Width != _captureSize.Width || capture.ContentSize.Height != _captureSize.Height)
                { Stop(); EnsureStarted(); return; }
                using var texture = CaptureInterop.GetTexture(capture.Surface);
                if (_lastFrame is null)
                {
                    var description = texture.Description;
                    description.BindFlags = BindFlags.None; description.MiscFlags = ResourceOptionFlags.None;
                    _lastFrame = _renderer!.Device.CreateTexture2D(description);
                }
                // Retain our own GPU frame; never keep a capture-pool buffer after the frame closes.
                _renderer!.CopyFrame(_lastFrame, texture);
                _dirty = true;
            }
            if (_dirty) Render();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail(error); }
    }

    private void Render()
    {
        if (_lastFrame is null || _renderer is null || _panel.ActualWidth <= 0 || _panel.ActualHeight <= 0) return;
        try
        {
            var origin = new CaptureInterop.Point();
            if (!CaptureInterop.ClientToScreen(_window, ref origin)) throw new InvalidOperationException("Cannot locate the island background.");
            _renderer.Render(_lastFrame, origin.X + _frame.Left - _monitorRect.Left, origin.Y + _frame.Top - _monitorRect.Top,
                _frame, _options, (float)(_frame.Width / _panel.ActualWidth), (float)(_frame.Height / _panel.ActualHeight));
            _dirty = false;
            if (!IsReady) { IsReady = true; StateChanged?.Invoke(this, EventArgs.Empty); }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Fail(error); }
    }
    private void OnCaptureClosed(GraphicsCaptureItem sender, object args) => Fail(new InvalidOperationException("Background capture ended."));
    // Explicit diagnostic only: stop sampling before restoring screenshot visibility,
    // so a native UI screenshot can show the rendered material without self-feedback.
    public void FreezeForDiagnosticCapture()
    {
        _diagnosticFrozen = true;
        _timer.Stop(); _session?.Dispose(); _session = null;
        _pool?.Dispose(); _pool = null;
        if (_excluded && CaptureInterop.SetWindowDisplayAffinity(_window, _priorAffinity)) _excluded = false;
    }
    private void Fail(Exception error) { Failure = error.ToString(); Stop(); StateChanged?.Invoke(this, EventArgs.Empty); }
    private void Stop()
    {
        ++_generation; _starting = false; _diagnosticFrozen = false; _timer.Stop();
        if (_item is not null) _item.Closed -= OnCaptureClosed;
        _session?.Dispose(); _session = null; _pool?.Dispose(); _pool = null; _item = null;
        _lastFrame?.Dispose(); _lastFrame = null;
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
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; _timer.Tick -= OnTick;
        Stop(); Children.Clear();
    }
}
