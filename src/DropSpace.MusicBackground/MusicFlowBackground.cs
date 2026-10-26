using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Lyricify.Backgrounds.AppleMusicInspired.Ios;
using Lyricify.Backgrounds.AppleMusicInspired.Ios.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Controls;

/// <summary>
/// A window-owned native texture/mesh background. It receives artwork and existing audio
/// observations; it never reads media sessions, opens an audio device, or downloads artwork.
/// All public methods except Dispose's idempotent check are called on the owning UI thread.
/// </summary>
public sealed class MusicFlowBackground : Grid, IDisposable
{
    private const int MaximumEncodedArtworkBytes = 16 * 1024 * 1024;
    private readonly SwapChainPanel _panel = new();
    private readonly AppleMusicIosBackgroundSettings _settings = new()
    { FrameRateLimit = 8, RenderScale = 0.75, RotationScale = 0.65, BassPulseScale = 0.6, BlurScale = 1.15 };
    private AppleMusicIosRenderer? _renderer;
    private SwapChainPanelPresenter? _presenter;
    private CancellationTokenSource? _renderLifetime;
    private byte[]? _artwork;
    private int _artworkGeneration;
    private bool _playing, _reducedMotion, _visible = true, _disposed, _renderingHooked, _failed;

    public MusicFlowBackground()
    {
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 19, 24, 31));
        IsHitTestVisible = false;
        Children.Add(_panel);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        _panel.CompositionScaleChanged += OnCompositionScaleChanged;
    }

    public bool IsReady { get; private set; }
    public long PresentedFrameCount => _renderer?.PresentedFrameCount ?? 0;
    public long FrameCount => PresentedFrameCount;
    public float AudioPulse => _renderer?.AudioPulse ?? 0;
    public bool IsRendering => _renderingHooked && !_failed && _visible;
    public string? LastFault { get; private set; }
    public event EventHandler<Exception>? Faulted;

    public async Task SetArtworkAsync(byte[]? encoded, CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        if (encoded?.Length > MaximumEncodedArtworkBytes) throw new InvalidDataException("Artwork exceeds the background input limit.");
        _artwork = encoded is { Length: > 0 } ? encoded.ToArray() : null;
        var generation = ++_artworkGeneration;
        if (_renderer is { } renderer) await ApplyArtworkAsync(renderer, _artwork, generation, cancellationToken);
    }

    public void SetPlaybackState(bool playing, bool reducedMotion, bool visible)
    {
        VerifyAccess();
        if ((_playing, _reducedMotion, _visible) == (playing, reducedMotion, visible)) return;
        _playing = playing; _reducedMotion = reducedMotion; _visible = visible;
        _settings.FrameRateLimit = reducedMotion ? 0 : playing ? 30 : 8;
        if (_renderer is { } renderer)
        {
            renderer.SetReducedMotion(reducedMotion);
            renderer.SetPresentationVisible(visible);
            renderer.ApplySettings(false);
            if (!playing || !visible || reducedMotion) renderer.UpdateSpectrum(0, false);
        }
        UpdateRenderingSubscription();
        RenderOnce();
    }

    /// <param name="amplitude">Normalized 0..1 amplitude from the existing fresh process-loopback frame.</param>
    /// <param name="hasData">False for unavailable, stale, system-wide, or stopped capture.</param>
    public void UpdateSpectrum(float amplitude, bool hasData)
    {
        VerifyAccess();
        _renderer?.UpdateSpectrum(amplitude, hasData && _playing && _visible && !_reducedMotion);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _renderer is not null) return;
        try
        {
            _failed = false;
            LastFault = null;
            _renderLifetime = new CancellationTokenSource();
            _presenter = new SwapChainPanelPresenter(_panel, () => IsReady = true);
            _renderer = new AppleMusicIosRenderer(_settings, false, () => _playing, _presenter);
            _renderer.Faulted += OnRendererFaulted;
            _renderer.SetReducedMotion(_reducedMotion);
            _renderer.SetPresentationVisible(_visible);
            _renderer.SetIsBehindLyrics(true);
            UpdateSize();
            _renderer.Initialize();
            UpdateRenderingSubscription();
            _ = ApplyInitialArtworkAsync(_renderer, _artworkGeneration);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ReleaseRenderer();
            ReportFault(error);
        }
    }

    private async Task ApplyInitialArtworkAsync(AppleMusicIosRenderer renderer, int generation)
    {
        try { await ApplyArtworkAsync(renderer, _artwork, generation, CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { ReportFault(error); }
    }

    private async Task ApplyArtworkAsync(AppleMusicIosRenderer renderer, byte[]? encoded, int generation, CancellationToken cancellationToken)
    {
        if (_renderLifetime is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _renderLifetime.Token);
        using var bitmap = await Task.Run(() => DecodeArtwork(encoded), linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (_disposed || generation != _artworkGeneration || renderer != _renderer) return;
        await renderer.SetArtworkAsync(bitmap, generation.ToString(System.Globalization.CultureInfo.InvariantCulture), linked.Token);
        if (!_disposed && generation == _artworkGeneration && renderer == _renderer) RenderOnce();
    }

    private static Bitmap DecodeArtwork(byte[]? encoded)
    {
        if (encoded is null)
        {
            // A neutral texture still goes through the same flowing mesh; absent audio
            // changes only pulse strength, never invents a beat.
            var fallback = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
            fallback.SetPixel(0, 0, Color.FromArgb(44, 61, 73));
            fallback.SetPixel(1, 0, Color.FromArgb(69, 57, 68));
            fallback.SetPixel(0, 1, Color.FromArgb(35, 48, 55));
            fallback.SetPixel(1, 1, Color.FromArgb(60, 67, 72));
            return fallback;
        }
        using var stream = new MemoryStream(encoded, false);
        using var source = new Bitmap(stream);
        if (source.Width > 16_384 || source.Height > 16_384 || (long)source.Width * source.Height > 64_000_000)
            throw new InvalidDataException("Artwork dimensions exceed the background decode limit.");
        var scale = Math.Min(1d, 300d / Math.Max(source.Width, source.Height));
        var decoded = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)), PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(decoded);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, new Rectangle(0, 0, decoded.Width, decoded.Height));
            return decoded;
        }
        catch { decoded.Dispose(); throw; }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => ReleaseRenderer();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) { UpdateSize(); RenderOnce(); }
    private void OnCompositionScaleChanged(SwapChainPanel sender, object args) { UpdateSize(); RenderOnce(); }
    private void UpdateSize()
    {
        _renderer?.SetVerticalLayout(ActualHeight > ActualWidth);
        _renderer?.Resize(ActualWidth, ActualHeight, _panel.CompositionScaleX, _panel.CompositionScaleY);
    }
    private void UpdateRenderingSubscription()
    {
        var shouldRender = !_disposed && !_failed && IsLoaded && _visible && !_reducedMotion && _renderer is not null;
        if (shouldRender == _renderingHooked) return;
        if (shouldRender) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = shouldRender;
    }
    private void OnRendering(object? sender, object args) => RenderOnce();
    private void RenderOnce()
    {
        if (_disposed || _failed || !_visible) return;
        try { _renderer?.Render(); }
        catch (Exception error) when (error is not OutOfMemoryException) { OnRendererFaulted(this, error); }
    }
    private void OnRendererFaulted(object? sender, Exception error)
    {
        _failed = true;
        UpdateRenderingSubscription();
        ReportFault(error);
    }
    private void ReportFault(Exception error)
    {
        if (_disposed) return;
        LastFault = error.GetType().Name + ": " + error.Message;
        Faulted?.Invoke(this, error);
    }
    private void ReleaseRenderer()
    {
        if (_renderingHooked) { CompositionTarget.Rendering -= OnRendering; _renderingHooked = false; }
        _renderLifetime?.Cancel(); _renderLifetime?.Dispose(); _renderLifetime = null;
        if (_renderer is { } renderer) { renderer.Faulted -= OnRendererFaulted; renderer.Dispose(); _renderer = null; }
        _presenter?.Dispose(); _presenter = null;
        IsReady = false;
    }
    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("The music background must be updated on its owning UI thread.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        VerifyAccess(); _disposed = true; ++_artworkGeneration;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; SizeChanged -= OnSizeChanged;
        _panel.CompositionScaleChanged -= OnCompositionScaleChanged;
        ReleaseRenderer(); _artwork = null; Children.Clear();
    }
}
