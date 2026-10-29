using DropSpace.Core.Compatibility;
using DropSpace.Core.Overlay;
using DropSpace.Core.Models;
using DropSpace.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Services;

/// <summary>
/// Owns the background-only liquid-glass surface and existing Desktop Acrylic fallback.
/// The fallback remains a solid brush on Windows 10, reduced effects, and high contrast.
/// </summary>
internal sealed class OverlayMaterialController : IDisposable
{
    private readonly SystemBackdropElement _backdrop;
    private readonly Border _fallback;
    private readonly IWindowsCapabilityService _capabilities;
    private bool _disposed;
    private IslandAcrylicBackdrop? _acrylic;
    private OverlayRegionSignature? _geometry;
    private readonly LiquidGlassSurface _glass;
    private IslandAppearanceSettings _appearance = new();
    private OverlayVisualPreferences? _preferences;
    private bool _visible;

    public OverlayMaterialController(
        SystemBackdropElement backdrop,
        Border fallback,
        IWindowsCapabilityService capabilities,
        nint window,
        Grid materialHost)
    {
        _backdrop = backdrop;
        _fallback = fallback;
        _capabilities = capabilities;
        _glass = new LiquidGlassSurface(window) { Opacity = 0 };
        materialHost.Children.Insert(0, _glass);
        _glass.StateChanged += OnGlassStateChanged;
    }

    public bool IsUsingDesktopAcrylic { get; private set; }

    public OverlayMaterialTier MaterialTier { get; private set; } = OverlayMaterialTier.Windows10Solid;

    public void Apply(OverlayVisualPreferences preferences)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _preferences = preferences;
        var glassRequested = _visible && _appearance.MaterialStyle == IslandMaterialStyle.LiquidGlass &&
            preferences.AdvancedEffectsEnabled && preferences.TransparencyEnabled &&
            !preferences.HighContrast && !preferences.IsRemoteSession;
        _glass.SetState(glassRequested, new GlassOptions((float)_appearance.LiquidGlassBlur,
            (float)_appearance.LiquidGlassRefraction, (float)_appearance.LiquidGlassDispersion,
            (float)_appearance.LiquidGlassDarkOverlay, _appearance.LiquidGlassBlackGradient));
        _glass.Opacity = glassRequested && _glass.IsReady ? 1 : 0;
        if (_glass.Opacity > 0)
        {
            _acrylic?.SetEnabled(false);
            _backdrop.Visibility = Visibility.Collapsed; _fallback.Visibility = Visibility.Collapsed;
            IsUsingDesktopAcrylic = false;
            MaterialTier = OverlayMaterialTier.LiquidGlass;
            return;
        }
        var requestedTier = preferences.MaterialTier;
        var canUseAcrylic = requestedTier is OverlayMaterialTier.DesktopAcrylic or OverlayMaterialTier.DesktopAcrylicFast &&
                            _capabilities.IsAvailable(WindowsCapability.DesktopAcrylic) &&
                            _capabilities.IsAvailable(WindowsCapability.TransientSystemBackdrop) &&
                            _capabilities.IsAvailable(WindowsCapability.CompositionEffects);
        try
        {
            if (canUseAcrylic)
            {
                _acrylic ??= new IslandAcrylicBackdrop();
                if (_geometry is { } geometry) _acrylic.SetGeometry(geometry);
                _backdrop.SystemBackdrop ??= _acrylic;
                _acrylic.SetEnabled(true);
            }
            else _acrylic?.SetEnabled(false);

            _backdrop.Visibility = canUseAcrylic ? Visibility.Visible : Visibility.Collapsed;
            _fallback.Visibility = canUseAcrylic ? Visibility.Collapsed : Visibility.Visible;
            // XAML ThemeResource owns the fallback color, including High Contrast.
            // Reusing a brush captured on construction would retain the previous theme.
            IsUsingDesktopAcrylic = canUseAcrylic;
            MaterialTier = canUseAcrylic
                ? requestedTier
                : preferences.HighContrast
                    ? OverlayMaterialTier.HighContrastSystemSurface
                    : preferences.IsWindows11OrLater
                        ? OverlayMaterialTier.Windows11Solid
                        : OverlayMaterialTier.Windows10Solid;
        }
        catch (Exception)
        {
            try { _acrylic?.SetEnabled(false); }
            catch (Exception cleanupError) { System.Diagnostics.Trace.TraceError("Acrylic cleanup failed: {0}", cleanupError); }
            _backdrop.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
            IsUsingDesktopAcrylic = false;
            MaterialTier = preferences.HighContrast
                ? OverlayMaterialTier.HighContrastSystemSurface
                : preferences.IsWindows11OrLater
                    ? OverlayMaterialTier.Windows11Solid
                    : OverlayMaterialTier.Windows10Solid;
        }
    }

    public void SetCornerRadius(CornerRadius radius)
    {
        // Transparent HWND backing preserves the actual antialiased edge.
        // Overpainting into a hard HRGN would discard its fractional coverage.
        _backdrop.Margin = new Thickness(0);
        _fallback.Margin = new Thickness(0);
        // The installed native Acrylic rounded clip is binary; coverage owns this edge.
        _backdrop.CornerRadius = new CornerRadius(0);
        _fallback.CornerRadius = radius;
    }

    internal void SetGeometry(OverlayRegionSignature geometry, double dpi = 1)
    {
        _geometry = geometry;
        _acrylic?.SetGeometry(geometry);
        _glass.SetGeometry(new GlassFrame(geometry.LeftPixels, geometry.TopPixels, geometry.WidthPixels,
            geometry.HeightPixels, geometry.TopRadiusPixels, geometry.BottomRadiusPixels, (float)dpi));
    }

    internal void SetAppearance(IslandAppearanceSettings appearance)
    {
        _appearance = appearance;
        if (_preferences is { } preferences) Apply(preferences);
    }
    internal void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (_preferences is { } preferences) Apply(preferences);
    }
    private void OnGlassStateChanged(object? sender, EventArgs args)
    {
        if (!_disposed && _preferences is { } preferences) Apply(preferences);
    }
    internal object CaptureGlassState() => new { ready = _glass.IsReady, failure = _glass.Failure,
        frames = _glass.PresentedFrames, requested = _appearance.MaterialStyle.ToString(), visible = _visible };
    internal void FreezeGlassForDiagnosticCapture() => _glass.FreezeForDiagnosticCapture();

    internal void SetMotion(IslandMotionBlurFrame frame)
    {
        if (!_disposed) _acrylic?.SetMotion(IsUsingDesktopAcrylic ? frame : IslandMotionBlurFrame.None);
    }

    internal object? CaptureMotionState() => _acrylic?.CaptureMotionState();
    internal void PrepareMotion() { if (!_disposed && IsUsingDesktopAcrylic) _acrylic?.PrepareMotion(); }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _glass.StateChanged -= OnGlassStateChanged;
        _glass.Dispose();
        _acrylic?.Dispose();
    }

}
