using DropSpace.Core.Compatibility;
using DropSpace.Core.Overlay;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Services;

/// <summary>
/// Applies the bounded Windows 11 Desktop Acrylic surface and owns its deterministic fallbacks.
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

    public OverlayMaterialController(
        SystemBackdropElement backdrop,
        Border fallback,
        IWindowsCapabilityService capabilities)
    {
        _backdrop = backdrop;
        _fallback = fallback;
        _capabilities = capabilities;
    }

    public bool IsUsingDesktopAcrylic { get; private set; }

    public OverlayMaterialTier MaterialTier { get; private set; } = OverlayMaterialTier.Windows10Solid;

    public void Apply(OverlayVisualPreferences preferences)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    internal void SetGeometry(OverlayRegionSignature geometry)
    {
        _geometry = geometry;
        _acrylic?.SetGeometry(geometry);
    }

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
        _acrylic?.Dispose();
    }

}
