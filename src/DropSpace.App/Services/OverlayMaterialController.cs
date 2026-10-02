using DropSpace.Core.Compatibility;
using DropSpace.Core.Overlay;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
    private readonly Brush? _normalFallbackBrush;
    private bool _disposed;
    private IslandAcrylicBackdrop? _acrylic;

    public OverlayMaterialController(
        SystemBackdropElement backdrop,
        Border fallback,
        IWindowsCapabilityService capabilities)
    {
        _backdrop = backdrop;
        _fallback = fallback;
        _capabilities = capabilities;
        _normalFallbackBrush = fallback.Background;
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
                _backdrop.SystemBackdrop ??= _acrylic;
                _acrylic.SetEnabled(true);
            }
            else _acrylic?.SetEnabled(false);

            _backdrop.Visibility = canUseAcrylic ? Visibility.Visible : Visibility.Collapsed;
            _fallback.Visibility = canUseAcrylic ? Visibility.Collapsed : Visibility.Visible;
            if (preferences.HighContrast)
            {
                _fallback.Background = GetSystemBrush(
                    "SystemControlBackgroundBaseLowBrush",
                    _normalFallbackBrush);
            }
            else
            {
                _fallback.Background = _normalFallbackBrush;
            }
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
            _acrylic?.SetEnabled(false);
            _backdrop.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
            _fallback.Background = _normalFallbackBrush;
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
        // XAML antialiasing and the integer native HRGN must not leave an
        // uncovered pixel of the WinUI host between the material and the halo.
        // Paint one physical pixel beyond that boundary; the unchanged native
        // region still owns the visible contour and mouse hit area.
        var scale = _backdrop.XamlRoot?.RasterizationScale ?? 1;
        var bleed = 1 / (double.IsFinite(scale) && scale > 0 ? scale : 1);
        var paintedRadius = new CornerRadius(radius.TopLeft + bleed, radius.TopRight + bleed,
            radius.BottomRight + bleed, radius.BottomLeft + bleed);
        _backdrop.Margin = new Thickness(-bleed);
        _fallback.Margin = new Thickness(-bleed);
        _backdrop.CornerRadius = paintedRadius;
        _fallback.CornerRadius = paintedRadius;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _acrylic?.Dispose();
    }

    private static Brush? GetSystemBrush(string key, Brush? fallback)
    {
        try
        {
            return Application.Current.Resources[key] as Brush ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
