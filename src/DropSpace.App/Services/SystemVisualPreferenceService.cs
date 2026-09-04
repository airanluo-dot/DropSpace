using DropSpace.Core.Compatibility;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;

namespace DropSpace.App.Services;

/// <summary>
/// Owns the process-wide Windows visual preference subscriptions used by transient surfaces.
/// It deliberately keeps system settings out of the per-frame motion path and marshals every
/// callback back to the owning UI dispatcher before changing visual consumers.
/// </summary>
public sealed class SystemVisualPreferenceService : IDisposable
{
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly IWindowsCapabilityService _capabilities;
    private readonly DispatcherQueue? _dispatcher;
    private bool _animationsSubscriptionActive;
    private bool _highContrastSubscriptionActive;
    private bool _disposed;

    public SystemVisualPreferenceService(
        IWindowsCapabilityService capabilities,
        DispatcherQueue? dispatcher = null)
    {
        _capabilities = capabilities;
        _dispatcher = dispatcher;
        TrySubscribePreferenceEvents();
        Current = ReadPreferences(OverlayMotionPreference.System);
    }

    public event EventHandler? Changed;

    public OverlayVisualPreferences Current { get; private set; }

    public OverlayVisualPreferences Resolve(OverlayMotionPreference preference) =>
        ReadPreferences(preference);

    public bool IsReducedMotion(OverlayMotionPreference preference) =>
        Resolve(preference).ReducedMotion;

    private void TrySubscribePreferenceEvents()
    {
        try
        {
            _uiSettings.AnimationsEnabledChanged += OnSystemVisualPreferenceChanged;
            _animationsSubscriptionActive = true;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Windows 10 1809 exposes AnimationsEnabled but not its change event. The initial
            // snapshot remains authoritative on that baseline.
        }

        try
        {
            _uiSettings.AdvancedEffectsEnabledChanged += OnSystemVisualPreferenceChanged;
            _uiSettings.ColorValuesChanged += OnSystemVisualPreferenceChanged;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // A session-isolated shell may expose the value but not the event source.
        }

        try
        {
            _accessibilitySettings.HighContrastChanged += OnSystemVisualPreferenceChanged;
            _highContrastSubscriptionActive = true;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Some headless/session-isolated Windows environments expose the value but do not
            // provide an event source. The initial snapshot remains authoritative.
        }
    }

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

        var acrylicSupported = false;
        var compositionSupported = false;
        var compositionFast = false;
        var remoteSession = false;
        try
        {
            var acrylic = _capabilities.Get(WindowsCapability.DesktopAcrylic);
            var composition = _capabilities.Get(WindowsCapability.CompositionEffects);
            acrylicSupported = acrylic.IsAvailable;
            compositionSupported = composition.IsAvailable;
            compositionFast = composition.IsFast;
            remoteSession = acrylic.IsRemoteSession;
        }
        catch (Exception)
        {
            // Material selection is fail-closed when the optional capability broker is
            // unavailable. Motion and the solid surface remain usable.
        }

        return new OverlayVisualPreferences(
            motion,
            advancedEffectsEnabled,
            highContrast,
            _capabilities.Snapshot.OperatingSystem.Build >= WindowsCompatibilityPolicy.Windows11Build,
            DesktopAcrylicSupported: acrylicSupported,
            CompositionEffectsSupported: compositionSupported,
            CompositionEffectsFast: compositionFast,
            TransparencyEnabled: advancedEffectsEnabled,
            IsRemoteSession: remoteSession);
    }

    private void OnSystemVisualPreferenceChanged(object? sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            PublishPreferenceChange();
            return;
        }

        if (!_dispatcher.TryEnqueue(PublishPreferenceChange))
        {
            // Dispatcher shutdown is a safe no-op. Dispose owns the final lifecycle boundary and
            // no preference callback may mutate UI after that boundary.
        }
    }

    private void PublishPreferenceChange()
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
        if (_animationsSubscriptionActive)
        {
            _uiSettings.AnimationsEnabledChanged -= OnSystemVisualPreferenceChanged;
        }

        _uiSettings.AdvancedEffectsEnabledChanged -= OnSystemVisualPreferenceChanged;
        _uiSettings.ColorValuesChanged -= OnSystemVisualPreferenceChanged;
        if (_highContrastSubscriptionActive)
        {
            _accessibilitySettings.HighContrastChanged -= OnSystemVisualPreferenceChanged;
        }
    }
}
