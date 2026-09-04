using DropSpace.Core.Compatibility;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Windows.UI.ViewManagement;

namespace DropSpace.App.Services;

/// <summary>
/// Owns the process-wide Windows visual preference subscriptions used by transient surfaces.
/// It deliberately keeps system settings out of the per-frame motion path.
/// </summary>
public sealed class SystemVisualPreferenceService : IDisposable
{
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly IWindowsCapabilityService _capabilities;
    private bool _highContrastSubscriptionActive;
    private bool _disposed;

    public SystemVisualPreferenceService(IWindowsCapabilityService capabilities)
    {
        _capabilities = capabilities;
        _uiSettings.AdvancedEffectsEnabledChanged += OnSystemVisualPreferenceChanged;
        _uiSettings.ColorValuesChanged += OnSystemVisualPreferenceChanged;
        try
        {
            _accessibilitySettings.HighContrastChanged += OnSystemVisualPreferenceChanged;
            _highContrastSubscriptionActive = true;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Some headless/session-isolated Windows environments expose the value but
            // do not provide an event source. The initial snapshot remains authoritative.
        }
        Current = ReadPreferences(OverlayMotionPreference.System);
    }

    public event EventHandler? Changed;

    public OverlayVisualPreferences Current { get; private set; }

    public OverlayVisualPreferences Resolve(OverlayMotionPreference preference)
    {
        var current = ReadPreferences(preference);
        return current;
    }

    public bool IsReducedMotion(OverlayMotionPreference preference) => Resolve(preference).ReducedMotion;

    private OverlayVisualPreferences ReadPreferences(OverlayMotionPreference preference)
    {
        var animationsEnabled = true;
        var advancedEffectsEnabled = true;
        var highContrast = false;
        try
        {
            animationsEnabled = _uiSettings.AnimationsEnabled;
            advancedEffectsEnabled = _uiSettings.AdvancedEffectsEnabled;
            highContrast = _accessibilitySettings.HighContrast;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // The safe fallback is reduced motion plus the opaque surface. The app can still
            // operate if the shell preference broker is temporarily unavailable.
            animationsEnabled = false;
            advancedEffectsEnabled = false;
        }

        var motion = preference switch
        {
            OverlayMotionPreference.Reduced => OverlayVisualPreferenceMode.Reduced,
            OverlayMotionPreference.Full => OverlayVisualPreferenceMode.Full,
            _ => animationsEnabled ? OverlayVisualPreferenceMode.Full : OverlayVisualPreferenceMode.Reduced,
        };
        return new OverlayVisualPreferences(
            motion,
            advancedEffectsEnabled,
            highContrast,
            _capabilities.Snapshot.OperatingSystem.Build >= WindowsCompatibilityPolicy.Windows11Build);
    }

    private void OnSystemVisualPreferenceChanged(object? sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        Current = ReadPreferences(OverlayMotionPreference.System);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _uiSettings.AdvancedEffectsEnabledChanged -= OnSystemVisualPreferenceChanged;
        _uiSettings.ColorValuesChanged -= OnSystemVisualPreferenceChanged;
        if (_highContrastSubscriptionActive)
        {
            _accessibilitySettings.HighContrastChanged -= OnSystemVisualPreferenceChanged;
        }
    }
}
