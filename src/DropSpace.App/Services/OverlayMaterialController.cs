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
    private readonly Border _stroke;
    private readonly IWindowsCapabilityService _capabilities;
    private readonly Brush? _normalFallbackBrush;
    private readonly Brush? _normalStrokeBrush;
    private bool _disposed;

    public OverlayMaterialController(
        SystemBackdropElement backdrop,
        Border fallback,
        Border stroke,
        IWindowsCapabilityService capabilities)
    {
        _backdrop = backdrop;
        _fallback = fallback;
        _stroke = stroke;
        _capabilities = capabilities;
        _normalFallbackBrush = fallback.Background;
        _normalStrokeBrush = stroke.BorderBrush;
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
                _backdrop.SystemBackdrop ??= new DesktopAcrylicBackdrop();
            }

            _backdrop.Visibility = canUseAcrylic ? Visibility.Visible : Visibility.Collapsed;
            _fallback.Visibility = canUseAcrylic ? Visibility.Collapsed : Visibility.Visible;
            if (preferences.HighContrast)
            {
                _fallback.Background = GetSystemBrush(
                    "SystemControlBackgroundBaseLowBrush",
                    _normalFallbackBrush);
                _stroke.BorderBrush = GetSystemBrush(
                    "SystemControlForegroundBaseHighBrush",
                    _normalStrokeBrush);
            }
            else
            {
                _fallback.Background = _normalFallbackBrush;
                _stroke.BorderBrush = _normalStrokeBrush;
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
            _backdrop.SystemBackdrop = null;
            _backdrop.Visibility = Visibility.Collapsed;
            _fallback.Visibility = Visibility.Visible;
            _fallback.Background = _normalFallbackBrush;
            _stroke.BorderBrush = _normalStrokeBrush;
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
        _backdrop.CornerRadius = radius;
        _fallback.CornerRadius = radius;
        _stroke.CornerRadius = radius;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backdrop.SystemBackdrop = null;
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
