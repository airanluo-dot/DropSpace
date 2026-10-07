using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Compatibility;
using DropSpace.Core.DragDrop;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using DropSpace.Core.Preview;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace DropSpace.App;

public sealed partial class OverlayWindow : Window
{
    private bool _pageReducedMotion;
    private readonly IslandPageTransition _pageTransition = new();
    private readonly Dictionary<FrameworkElement, TranslateTransform> _pageOffsets = [];
    private double ExpandedScale => OverlayPlacementPolicy.FitContentScale(
        _monitor.EffectiveWorkWidth, _monitor.EffectiveWorkHeight, _monitor.Scale,
        OverlayPlacementPolicy.MaximumSurfaceWidthDips, OverlayPlacementPolicy.MaximumSurfaceHeightDips,
        _mediaViewModel.Settings.IslandAppearance.ExpandedScale);
    private double HostContentScale => Math.Max(1, Math.Max(ExpandedScale, _mediaViewModel.Settings.IslandAppearance.CompactScale));
    private double _compactSurfaceWidth;
    private double HostWidth => Math.Max(OverlayPlacementPolicy.HostWidthDips * HostContentScale, _compactSurfaceWidth + 40);
    private static readonly TimeSpan AnimationTimerInterval = TimeSpan.FromMilliseconds(16);
    private double HostHeight => OverlayPlacementPolicy.GetMinimumHostHeightDips(_monitor.Scale, HostContentScale);
    private readonly OverlayViewModel _viewModel;
    private readonly IAppStringLocalizer _strings;
    private readonly MonitorDescriptor _monitor;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly Action _openMainWindow;
    private readonly ILogger<OverlayWindow> _logger;
    private readonly DragActivationCallbacks _visualDragCallbacks;
    private readonly OleDragDropService _dragDropService;
    private readonly QuickActionDialogService _quickActionDialog;
    private readonly nint _windowHandle;
    private readonly OverlayTransparentHostController _transparentHost;
    private readonly SystemVisualPreferenceService _visualPreferences;
    private readonly OverlayMaterialController _materialController;
    private readonly OverlayCompositionAnimator _compositionAnimator;
    private readonly OverlayNativeRegionController _nativeRegionController;
    private readonly IslandGlowController _glow;
    private bool _glowRefreshPending;
    private readonly OverlayMotionOrchestrator _motion;
    private OleDropTargetRegistration? _nativeDropTarget;
    private OverlayState _previousState = OverlayState.Hidden;
    private long _lastFrameTimestamp;
    private long _lastAnimationGlowRefresh;
    private bool _isActiveWindow;
    private bool _isVisible;
    private bool _hasFrameSubscription;
    private bool _displayAnimationFrames;
    private readonly OverlayFramePacer _framePacer = new();
    private readonly IslandMotionBlurPolicy _motionBlurPolicy = new();
    // Private diagnostic control; no settings or steady-state polling.
    private bool _motionBlurEnabled = true;
    private IslandMotionPhase _motionBlurPhase;
    private bool _hideWhenSettled;
    private long _dismissalGeneration;
    // Explicit local diagnostic sessions only. No lyric, file or account text is recorded.
    private readonly bool _islandTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("DROPSPACE_ISLAND_TRACE"), "1", StringComparison.Ordinal);
    private string? _islandTracePresentation;
    private long _islandTraceSequence;
    private bool _islandTraceDismissalActive;
    private long _islandTraceDismissalGeneration;
    private int _islandTraceOpacityQuarter;
    private bool _suppressedForFullscreen;
    private bool _forceFullscreenPresentation;
    private OverlayState _presentedState = OverlayState.Hidden;
    private long _lastTopmostFailureLog;
    private long _regionFailureCount;
    private bool _nativeWindowSafeToShow;
    private readonly bool _supportsModernDwmAttributes;
    private long _lastNativeRecoveryAttempt;
    private readonly string _nativeConfigurationDiagnostics;
    private string _lastNativeFailureDiagnostics = "none";
    private readonly int _operatingSystemBuild;
    private bool _visualDragActive;
    private long _visualDragGeneration;
    private readonly CancellationTokenSource _windowLifetime = new();
    private OverlayResolvedPlacement _resolvedPlacement;
    private OverlayVisualPhase _visualPhase = OverlayVisualPhase.Invisible;
    private readonly OverlayPlacementEditSession _placementEdit = new();
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly TypedEventHandler<DispatcherQueueTimer, object> _animationTimerHandler;
    private bool _placementEditActive;
    private readonly DispatcherQueueTimer _rightHoldTimer;
    private Microsoft.UI.Xaml.Input.Pointer? _rightHoldPointer;
    private bool _suppressedForPlacementEdit;
    private TaskCompletionSource<object?>? _motionSettled;
    private int _positionedHostWidthPixels = -1;
    private int _positionedHostHeightPixels = -1;
    private int _positionedHostLeftPixels = int.MinValue;
    private int _positionedHostTopPixels = int.MinValue;
    private bool? _noActivateApplied;
    private bool _nativeWindowShown;
    private readonly DropSpace.Core.Island.IslandExperienceCoordinator _experience;
    private readonly MediaViewModel _mediaViewModel;
    private readonly WidgetViewModel _widgetViewModel;
    private OverlaySnapshot? _presentationSnapshot;
    private bool _mediaGeometryRefreshPending;
    private bool _preparingMediaGeometry;
    private readonly long _compactPanelVisibilityToken;
    private readonly long _expandedPanelVisibilityToken;
    private MediaSessionSnapshot? _lastGlowSession;
    private IslandGlowTransfer? _glowTransfer;

    internal IslandGlowTransfer? CaptureGlowHandoff()
    {
        if (!_isVisible || !GlowContinuationAllowed() || _lastGlowSession is not { } session ||
            !session.IsSameTrack(_mediaViewModel.Session) || _glow.CaptureVisualState() is not { } state) return null;
        return new(new LyricsGlowHandoff(MonitorId, session with { Artwork = null }, state), Stopwatch.GetTimestamp());
    }

    internal void StageGlowHandoff(IslandGlowTransfer transfer) => _glowTransfer = transfer;

    private bool GlowContinuationAllowed()
    {
        var preferences = _visualPreferences.Current;
        return !_closing && !_suppressedForPlacementEdit && !_placementEditActive && !_suppressedForFullscreen &&
            _mediaViewModel.IsPlaying && _mediaViewModel.Settings.Lyrics.GlowMode is LyricsGlowMode.AiLyrics or LyricsGlowMode.Music &&
            !preferences.HighContrast && preferences.AdvancedEffectsEnabled;
    }

    public OverlayWindow(
        OverlayViewModel viewModel,
        IAppStringLocalizer strings,
        MonitorDescriptor monitor,
        MonitorLayoutService monitorLayout,
        IWindowsCapabilityService capabilities,
        OleDragDropService dragDropService,
        QuickActionDialogService quickActionDialog,
        DragActivationCallbacks dragCallbacks,
        Action openMainWindow,
        ILogger<OverlayWindow> logger,
        SystemVisualPreferenceService visualPreferences,
        DropSpace.Core.Island.IslandExperienceCoordinator experience,
        MediaViewModel mediaViewModel,
        WidgetViewModel widgetViewModel,
        ClipboardIslandViewModel clipboardViewModel,
        SystemActivityViewModel systemActivityViewModel)
    {
        _logger = logger;
        try
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
            _transparentHost = new OverlayTransparentHostController(_windowHandle);
            Closed += OnTransparentHostClosed;
            _viewModel = viewModel;
            _widgetViewModel = widgetViewModel;
            _strings = strings;
            _monitor = monitor;
            _monitorLayout = monitorLayout;
            _openMainWindow = openMainWindow;
            _visualDragCallbacks = dragCallbacks;
            _dragDropService = dragDropService;
            _quickActionDialog = quickActionDialog;
            _visualPreferences = visualPreferences;
            _experience = experience; _mediaViewModel = mediaViewModel;
            _operatingSystemBuild = capabilities.Snapshot.OperatingSystem.Build;
            _animationTimerHandler = OnTimerAnimationFrame;
            _animationTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _animationTimer.Interval = TimeSpan.FromMilliseconds(16);
            _animationTimer.IsRepeating = true;
            _animationTimer.Tick += _animationTimerHandler;
            try
            {
                InitializeComponent();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Overlay-window XAML initialization failed.", exception);
            }

            XamlResourceOverride.Apply(this, "OverlayWindow");
            Root.DataContext = viewModel;
            _ = LoadBrandLogoAsync();
            _rightHoldTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _rightHoldTimer.Interval = OverlayPlacementEditSession.HoldDuration;
            _rightHoldTimer.IsRepeating = false;
            _rightHoldTimer.Tick += (_, _) =>
            {
                if (_rightHoldPointer is not null && _mediaViewModel.Settings.IslandAppearance.RightClickHoldToMove)
                    PlacementEditRequested?.Invoke(this, MonitorId);
            };
            Surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSurfacePointerPressed), true);
            Surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSurfacePointerReleased), true);
            Surface.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => { _rightHoldTimer.Stop(); _rightHoldPointer = null; }), true);
            Surface.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
            {
                if (_placementEditActive) return;
                _rightHoldTimer.Stop(); _rightHoldPointer = null;
            }), true);
            MusicCompact.SetActive(false);
            _compactPanelVisibilityToken = CompactPanel.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, OnCompactPanelVisibilityChanged);
            MusicCompact.ViewModel = mediaViewModel;
            MusicExpanded.SetActive(false);
            _expandedPanelVisibilityToken = ExpandedPanel.RegisterPropertyChangedCallback(
                UIElement.VisibilityProperty, OnExpandedPanelVisibilityChanged);
            MusicExpanded.ViewModel = mediaViewModel;
            WidgetsExpanded.ViewModel = widgetViewModel;
            WidgetsExpanded.PinnedRequested += (_, _) => { _openMainWindow(); _experience.Collapse(); _viewModel.Collapse(); };
            ClipboardExpanded.ViewModel = clipboardViewModel;
            ActivityCompact.DataContext = systemActivityViewModel;
            MusicCompact.IdealWidthChanged += OnMediaGeometryChanged;
            _materialController = new OverlayMaterialController(
                AcrylicBackdrop,
                FallbackSurface,
                capabilities);
            _compositionAnimator = new OverlayCompositionAnimator(
                Surface,
                CompactPanel,
                DragPanel,
                ExpandedPanel,
                SurfaceContent,
                InteractionTintOverlay);
            _motion = new OverlayMotionOrchestrator(OverlayMotionValues.Hidden, _compositionAnimator);
            _materialController.Apply(_visualPreferences.Resolve(viewModel.MotionPreference));
            _visualPreferences.Changed += OnSystemVisualPreferencesChanged;

            var presenter = AppWindow.Presenter as OverlappedPresenter
                ?? throw new InvalidOperationException("Overlay window requires its existing overlapped presenter.");
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            AppWindow.IsShownInSwitchers = false;
            SystemBackdrop = new IslandTransparentBackdrop();
            _nativeRegionController = new OverlayNativeRegionController(_windowHandle);
            _glow = new IslandGlowController(_windowHandle, _monitor.Scale, DispatcherQueue.GetForCurrentThread(), logger);
            _mediaViewModel.PropertyChanged += OnGlowMediaChanged;
            MusicCompact.TranslationVisibilityChanged += OnGlowTranslationVisibilityChanged;
            MusicExpanded.TranslationVisibilityChanged += OnGlowTranslationVisibilityChanged;
            _supportsModernDwmAttributes = capabilities.IsAvailable(WindowsCapability.ModernDwmAttributes);
            var nativeConfiguration = OverlayWindowInterop.ConfigureVisualWindow(
                _windowHandle,
                capabilities.IsAvailable(WindowsCapability.ModernDwmAttributes));
            foreach (var failure in nativeConfiguration.Failures)
            {
                LogNativeFailure(failure);
            }
            _nativeConfigurationDiagnostics = nativeConfiguration.Failures.Count == 0
                ? "none"
                : string.Join(
                    "|",
                    nativeConfiguration.Failures.Select(failure =>
                        $"{failure.Operation}:critical={failure.Critical},win32={failure.Win32Error},hr=0x{failure.HResult:X8}"));
            _resolvedPlacement = ResolvePlacement(
                FileDragWakeMode.SmartExperimental,
                new OverlayMonitorPlacement(OverlayPlacementMode.Automatic, 0, 0));
            var hostGeometrySafe = PositionFixedHost();
            // A newly-created WinUI HWND can report its pre-layout client size until the first
            // show/layout pass. Keep the native configuration result authoritative here and
            // revalidate the fixed client surface immediately before every visible transition.
            _nativeWindowSafeToShow = nativeConfiguration.IsSafeToShow;
            if (!hostGeometrySafe)
            {
                _logger.LogWarning(
                    "Overlay HWND {WindowHandle} on monitor {MonitorId} did not report its fixed client geometry during initial construction; it will be revalidated before showing.",
                    _windowHandle,
                    _monitor.Id);
            }
            if (!nativeConfiguration.IsSafeToShow)
            {
                _logger.LogError(
                    "Overlay HWND {WindowHandle} on monitor {MonitorId} will remain hidden because its borderless native configuration was not safe to show.",
                    _windowHandle,
                    _monitor.Id);
            }
            if (!_nativeRegionController.ApplyEmpty(out var emptyRegionFailure))
            {
                LogNativeFailure(emptyRegionFailure);
                _nativeWindowSafeToShow = false;
            }
            TraceIslandEvent("native-hide-initial");
            if (!OverlayWindowInterop.Hide(_windowHandle, out var hideFailure))
            {
                LogNativeFailure(hideFailure);
                _nativeWindowSafeToShow = false;
            }
        }
        catch
        {
            StopAnimationFrames();
            Closed -= OnTransparentHostClosed;
            try { _transparentHost?.Dispose(); }
            catch (Win32Exception exception) { _logger.LogError(exception, "Overlay erase-hook cleanup failed during construction."); }
            try { Close(); }
            catch (Exception exception) { _logger.LogError(exception, "Failed to close an incompletely initialized overlay."); }
            throw;
        }
    }

    private void OnTransparentHostClosed(object sender, WindowEventArgs args)
    {
        _windowClosed = true;
        _closing = true;
        StopAnimationFrames();
        Closed -= OnTransparentHostClosed;
        try { _transparentHost?.Dispose(); }
        catch (Win32Exception exception) { _logger.LogError(exception, "Overlay erase-hook cleanup failed; the callback remains retained until HWND destruction."); }
    }

    public string MonitorId => _monitor.Id;

    internal void ApplyTheme(ThemePreference preference) => Root.RequestedTheme = preference switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public bool IsPlacementEditing => _placementEditActive;

    internal string NativeVisibilityDiagnostics =>
        $"active={_isActiveWindow},safe={_nativeWindowSafeToShow},visible={_isVisible},phase={_visualPhase}," +
        $"regions={RegionFailureCount},regionSkips={_nativeRegionController.SkippedUpdates}," +
        $"acrylic={_materialController.IsUsingDesktopAcrylic},configuration={_nativeConfigurationDiagnostics}," +
        $"lastFailure={_lastNativeFailureDiagnostics}";

    public event EventHandler<OverlayPlacementEditEventArgs>? PlacementCommitted;
    public event EventHandler<string>? PlacementEditRequested;

    public event EventHandler? PlacementCancelled;

    internal bool HasActiveFrameSubscription => _hasFrameSubscription;

    internal long RegionFailureCount => Interlocked.Read(ref _regionFailureCount);

    internal void VerifyLocalizedResources()
    {
        VerifyResourceValue(Title, "OverlayWindow.Title");
        VerifyResourceValue(ExpandedTitleText.Text, "OverlayExpandedTitle.Text");
        VerifyResourceValue(ExpandedSubtitleText.Text, "OverlayExpandedSubtitle.Text");
        VerifyResourceValue(RemoveHintText.Text, "OverlayRemoveHint.Text");
        VerifyResourceValue(OpenMainButton.Content, "OverlayOpenMainButton.Content");
        VerifyResourceValue(DropTargetTitleText.Text, "OverlayDropTargetTitle.Text");
        VerifyResourceValue(PlacementEditHintText.Text, "OverlayPlacementEditHint.Text");
        if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(CompactPanel)))
        {
            throw new InvalidOperationException("Localized overlay accessibility name did not resolve.");
        }
    }

    internal void VerifyTransientNativeRecoveryForSmoke()
    {
        var originalHandle = _windowHandle;
        HideForNativeFailure();
        _lastNativeRecoveryAttempt = Environment.TickCount64;
        EnsureVisualHostShown(false);
        if (_nativeWindowSafeToShow || _isVisible)
            throw new InvalidOperationException("A backoff-protected native failure must remain hidden.");
        _lastNativeRecoveryAttempt = 0;
        EnsureVisualHostShown(false);
        if (!_nativeWindowSafeToShow || !_nativeWindowShown || !_isVisible || _windowHandle != originalHandle)
            throw new InvalidOperationException("A transient native failure did not recover on the same HWND.");
        var wasClosing = _closing;
        try
        {
            _closing = true;
            if (TryRecoverNativeSurface()) throw new InvalidOperationException("Closing windows must not recover.");
        }
        finally { _closing = wasClosing; }
        HideImmediately();
        VerifyGlowHandoffLifecycleForSmoke();
    }

    private void VerifyGlowHandoffLifecycleForSmoke()
    {
        var wasSuppressed = _suppressedForFullscreen;
        var snapshot = new LyricsGlowVisualState(.2, 3, .5, .3, .2, .1, .4, .5, .2);
        IslandGlowTransfer Transfer() => new(new LyricsGlowHandoff(MonitorId, _mediaViewModel.Session, snapshot), Stopwatch.GetTimestamp());
        try
        {
            // Exercise the actual native first-show order, including its initial
            // transparent ApplyMotionFrame before _isVisible becomes true.
            var pending = Transfer();
            StageGlowHandoff(pending);
            EnsureVisualHostShown(false);
            if (!_isVisible || !ReferenceEquals(pending, _glowTransfer))
                throw new InvalidOperationException("Pre-show geometry discarded the staged glow continuation.");
            HideImmediately();
            StageGlowHandoff(Transfer());
            BeginFullscreenSuppression(_viewModel.Snapshot, FileDragWakeMode.Disabled);
            if (_glowTransfer is not null)
                throw new InvalidOperationException("Initial fullscreen suppression retained a stale glow continuation.");
            _glow.VerifyFrameCaptureFenceForSmoke();
        }
        finally
        {
            _suppressedForFullscreen = wasSuppressed;
            HideImmediately();
        }
    }

    internal VisibleWindowProbe ProbeVisibleCenter()
    {
        var values = _motion.Current.ProjectToSafeRange();
        var x = _resolvedPlacement.HostLeftPixels + ToPixels(HostWidth / 2);
        var y = _resolvedPlacement.HostTopPixels + ToPixels(values.TopOffset + values.Height / 2);
        var probe = OverlayWindowInterop.ProbeWindowAtPoint(_windowHandle, x, y);
        _logger.LogInformation(
            "Visible Overlay center probe on monitor {MonitorId}: point {X},{Y}, root HWND {RootWindow}, WindowFromPoint {DiscoveredWindow}, class {WindowClassName}, root-or-descendant={Owned}.",
            _monitor.Id,
            x,
            y,
            probe.RootWindow,
            probe.DiscoveredWindow,
            probe.WindowClassName,
            probe.IsRootOrDescendant);
        return probe;
    }

    private void VerifyResourceValue(object? actual, string key)
    {
        if (!string.Equals(actual as string, _strings.Get(key), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Localized Overlay XAML resource '{key}' did not resolve.");
        }
    }

    private void LogNativeFailure(OverlayNativeFailure? failure)
    {
        if (failure is null)
        {
            return;
        }

        _lastNativeFailureDiagnostics =
            $"{failure.Operation}:critical={failure.Critical},win32={failure.Win32Error},hr=0x{failure.HResult:X8}";
        var level = failure.Critical ? LogLevel.Error : LogLevel.Warning;
        _logger.Log(
            level,
            "Overlay native operation {Operation} failed on monitor {MonitorId}, HWND {WindowHandle}, scale {Scale:F2}, OS build {OperatingSystemBuild}; win32Error={Win32Error}, hresult=0x{HResult:X8}.",
            failure.Operation,
            _monitor.Id,
            _windowHandle,
            _monitor.Scale,
            _operatingSystemBuild,
            failure.Win32Error,
            failure.HResult);
    }

    internal Task RunSyntheticCfHDropAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        var values = _motion.Current.ProjectToSafeRange();
        var point = new NativePoint(
            _monitor.Left + _monitor.Width / 2,
            _monitor.Top + ToPixels(values.TopOffset + values.Height / 2));
        var target = _nativeDropTarget
            ?? throw new InvalidOperationException("The visible Overlay OLE target is not currently registered.");
        return target.RunSyntheticCfHDropAsync(paths, point, cancellationToken);
    }

    internal bool TryEnsureNativeDropTargetForSmoke()
    {
        if (!_isVisible || !_nativeWindowSafeToShow ||
            _presentedState is not (OverlayState.Compact or OverlayState.Expanded))
        {
            return false;
        }

        EnsureNativeDropTargetRegistered();
        return _nativeDropTarget is not null;
    }

    internal long RunGeometryStress(int cycles)
    {
        if (cycles is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(cycles));
        }

        var failuresBefore = RegionFailureCount;
        var topOffset = OverlayPlacementPolicy.GetTopOffsetDips(
            FileDragWakeMode.SmartExperimental,
            _monitor.Scale);
        var compact = CreateMotionTarget(OverlayState.Compact, topOffset);
        var ready = CreateMotionTarget(OverlayState.DragReady, topOffset);
        var expanded = CreateMotionTarget(OverlayState.Expanded, topOffset);
        var controller = new OverlayMotionController(compact);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            controller.SetTarget(ready, reducedMotion: false);
            ApplyStressFrames(controller, 2 + cycle % 5);
            controller.SetTarget(compact, reducedMotion: false);
            ApplyStressFrames(controller, 1 + cycle % 4);
            controller.SetTarget(expanded, reducedMotion: false);
            ApplyStressFrames(controller, 1 + cycle % 3);
            controller.SetTarget(compact, reducedMotion: false);
            ApplyStressFrames(controller, 2 + cycle % 6);
        }

        for (var frame = 0; frame < 600 && controller.IsAnimating; frame++)
        {
            ApplyStressFrames(controller, 1);
        }

        if (controller.IsAnimating || !controller.Current.IsApiSafe())
        {
            throw new InvalidOperationException("The real overlay geometry stress did not settle to an API-safe frame.");
        }

        return RegionFailureCount - failuresBefore;
    }

    private void ApplyStressFrames(OverlayMotionController controller, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            controller.Step(TimeSpan.FromMilliseconds(16));
            if (!controller.Current.IsApiSafe())
            {
                throw new InvalidOperationException($"Unsafe overlay motion frame: {controller.Current}.");
            }

            if (!ApplyMotionFrame(controller.Current))
            {
                throw new InvalidOperationException("The overlay native region could not be applied during geometry stress.");
            }
        }
    }

    public void ApplySnapshot(
        OverlaySnapshot snapshot,
        bool isActiveWindow,
        bool activationEnabled,
        FileDragWakeMode wakeMode,
        OverlayMonitorPlacement placement)
    {
        if (_closing) return;
        _presentationSnapshot = snapshot;
        var page = _experience.Current.Page;
        _pageReducedMotion = IsReducedMotion();
        _pageTransition.Select(page, _pageReducedMotion || snapshot.State != OverlayState.Expanded || _motion.Current.ExpandedContent < 0.01);
        ApplyPageTransition();
        PreviousPageRail.Visibility = page == DropSpace.Core.Island.IslandPage.Widgets ? Visibility.Collapsed : Visibility.Visible;
        NextPageRail.Visibility = page == DropSpace.Core.Island.IslandPage.Clipboard ? Visibility.Collapsed : Visibility.Visible;
        OtherPageCollapse.Visibility = page == DropSpace.Core.Island.IslandPage.Files ? Visibility.Collapsed : Visibility.Visible;
        var mediaCompact = _experience.Current.CompactContent == DropSpace.Core.Island.IslandContentKind.Music;
        var activityCompact = _experience.Current.CompactContent is DropSpace.Core.Island.IslandContentKind.Notification or DropSpace.Core.Island.IslandContentKind.Volume;
        ActivityCompact.Visibility = activityCompact ? Visibility.Visible : Visibility.Collapsed;
        MusicCompact.Visibility = mediaCompact ? Visibility.Visible : Visibility.Collapsed;
        var variant = _experience.Current.Variant;
        FileCompactContent.Visibility = variant is IslandPresentationVariant.SingleFile or IslandPresentationVariant.MultipleFiles ? Visibility.Visible : Visibility.Collapsed;
        EmptyWakeLogo.Visibility = (variant is IslandPresentationVariant.EmptyWake or IslandPresentationVariant.Idle) &&
            _mediaViewModel.Settings.IslandAppearance.ShowLogoWhenIdle
            ? Visibility.Visible : Visibility.Collapsed;
        MusicCompact.SetAvailableWidth(_monitor.EffectiveWorkWidth / _monitor.Scale /
            _mediaViewModel.Settings.IslandAppearance.CompactScale - 36);
        // Hidden islands retain dirty media state without doing presentation work.
        // Resolve it before selecting the first visible compact geometry.
        if (isActiveWindow && activationEnabled && mediaCompact && snapshot.State == OverlayState.Compact)
        {
            var wasPreparingMediaGeometry = _preparingMediaGeometry;
            _preparingMediaGeometry = true;
            try { MusicCompact.RefreshForPresentation(); }
            finally { _preparingMediaGeometry = wasPreparingMediaGeometry; }
        }
        if (isActiveWindow && activationEnabled && snapshot.State == OverlayState.Expanded && page == IslandPage.Music)
            MusicExpanded.RefreshForPresentation();
        var naturalWidth = mediaCompact ? MusicCompact.IdealIslandWidth : activityCompact ? 400 :
            variant == IslandPresentationVariant.SingleFile ? 340 : variant == IslandPresentationVariant.MultipleFiles ? 200 : variant == IslandPresentationVariant.EmptyWake ? 88 : 180;
        var naturalHeight = mediaCompact ? MusicCompact.IdealIslandHeight : activityCompact ? 80 :
            variant is IslandPresentationVariant.SingleFile or IslandPresentationVariant.MultipleFiles ? 64 : 40;
        var compactScale = OverlayPlacementPolicy.FitContentScale(_monitor.EffectiveWorkWidth, _monitor.EffectiveWorkHeight,
            _monitor.Scale, naturalWidth, naturalHeight, _mediaViewModel.Settings.IslandAppearance.CompactScale);
        _compactSurfaceWidth = naturalWidth * compactScale;
        CompactPanel.Padding = new Thickness(0);
        CompactContentRoot.Width = naturalWidth; CompactContentRoot.Height = naturalHeight;
        CompactContentRoot.Padding = new Thickness(mediaCompact ? 14 : 18, 0, mediaCompact ? 14 : 18, 0);
        CompactContentScaleHost.Width = naturalWidth * compactScale; CompactContentScaleHost.Height = naturalHeight * compactScale;
        MusicCompact.Width = MusicCompact.IdealIslandWidth - 28;
        MusicCompact.Height = MusicCompact.IdealIslandHeight;
        MusicCompact.HorizontalAlignment = HorizontalAlignment.Center;
        MusicCompact.RenderTransform = null;
        if (_suppressedForPlacementEdit)
        {
            HideImmediately();
            return;
        }

        if (_placementEditActive)
        {
            _resolvedPlacement = ResolvePlacement(wakeMode, new OverlayMonitorPlacement(
                OverlayPlacementMode.Custom,
                _placementEdit.Preview.X,
                _placementEdit.Preview.Y));
            if (!PositionFixedHost())
            {
                HideForNativeFailure();
            }
            return;
        }

        if (_visualPhase == OverlayVisualPhase.Exiting && snapshot.State is not (OverlayState.Dismissing or OverlayState.Hidden))
        {
            _visualPhase = OverlayVisualPhase.Reversing;
        }
        else if (snapshot.State is OverlayState.Dismissing or OverlayState.Hidden)
        {
            _visualPhase = OverlayVisualPhase.Exiting;
        }
        else if (!_isVisible)
        {
            _visualPhase = OverlayVisualPhase.Entering;
        }

        _isActiveWindow = isActiveWindow && activationEnabled;
        if (!_isActiveWindow)
        {
            HideImmediately();
            return;
        }

        var settings = _mediaViewModel.Settings;
        var fullscreen = FullscreenOverlayPolicy.Resolve(snapshot.State,
            settings.IslandAppearance.ForceShowOverFullscreen,
            _monitorLayout.IsForegroundFullscreen(_monitor));
        _forceFullscreenPresentation = fullscreen.KeepTopmost;
        snapshot = snapshot with { State = fullscreen.State };
        _presentedState = fullscreen.State;
        // The coordinator has already applied permission and the unified hide delay.

        if (_suppressedForFullscreen)
        {
            _logger.LogInformation(
                "Full-screen suppression ended on monitor {MonitorId}; restoring the overlay with its current spring state.",
                _monitor.Id);
        }

        _suppressedForFullscreen = false;
        _hideWhenSettled = snapshot.State == OverlayState.Dismissing;
        if (_hideWhenSettled) _dismissalGeneration = _experience.HideGeneration;
        TraceIslandPresentation(snapshot.State);
        _resolvedPlacement = ResolvePlacement(wakeMode, placement);
        if (!PositionFixedHost())
        {
            HideForNativeFailure();
            return;
        }

        if (snapshot.State == OverlayState.Hidden)
        {
            HideImmediately();
            _previousState = snapshot.State;
            return;
        }

        var topOffset = _resolvedPlacement.SurfaceTopOffsetDips;
        var target = CreateMotionTarget(snapshot.State, topOffset);
        if (snapshot.State == OverlayState.Compact)
            target = Create(naturalWidth * compactScale, naturalHeight * compactScale, topOffset,
                Math.Min(naturalWidth, naturalHeight) * compactScale / 2, 1, 0, 0);

        // Hidden has TopOffset=0 because it is independent of monitor placement.
        // Anchor it while the body is still transparent; otherwise opacity can lead
        // the top-offset spring and expose a clipped halo during the first frames.
        var anchored = OverlayPlacementPolicy.AnchorInvisibleSurface(_motion.Current, _resolvedPlacement);
        if (anchored != _motion.Current) _motion.SnapTo(anchored);
        EnsureVisualHostShown(fullscreen.AllowActivation);
        MaintainFullscreenVisibility();
        _mediaViewModel.SetPresentationVisible(this, _isVisible && (snapshot.State == OverlayState.Compact && mediaCompact || snapshot.State == OverlayState.Expanded && page == DropSpace.Core.Island.IslandPage.Music));
        WidgetsExpanded.SetActive(snapshot.State == OverlayState.Expanded && page == DropSpace.Core.Island.IslandPage.Widgets);
        ClipboardExpanded.SetActive(snapshot.State == OverlayState.Expanded && page == DropSpace.Core.Island.IslandPage.Clipboard);
        PrepareContentForTarget(target);
        if (_previousState == OverlayState.DragReady && snapshot.State == OverlayState.Compact)
        {
            _motion.PulseDropTarget(OverlayMotionTokens.DropConfirmationScale);
        }

        var reducedMotion = IsReducedMotion();
        var expandedTransition = (_previousState == OverlayState.Expanded) != (snapshot.State == OverlayState.Expanded);
        if (expandedTransition && !reducedMotion)
        {
            ResetMotionBlur();
            if (_motionBlurEnabled) _materialController.PrepareMotion();
            _motionBlurPhase = snapshot.State == OverlayState.Expanded
                ? IslandMotionPhase.Opening : IslandMotionPhase.Closing;
        }
        else if (reducedMotion) ResetMotionBlur();
        _motion.SetTarget(target, reducedMotion);
        StartAnimationFrames(preferDisplayCadence: !reducedMotion && expandedTransition);
        _previousState = snapshot.State;
        UpdateGlowTarget();
    }

    private bool _closing;
    private bool _shutdownStarted;
    private bool _windowClosed;

    public void CloseForShutdown()
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _glowTransfer = null;
        _closing = true;
        StopAnimationFrames();
        _animationTimer.Tick -= _animationTimerHandler;
        _windowLifetime.Cancel();
        Views.ContentDialogLifetime.RetireRoot(Root.XamlRoot);
        _mediaViewModel.PropertyChanged -= OnGlowMediaChanged;
        MusicCompact.TranslationVisibilityChanged -= OnGlowTranslationVisibilityChanged;
        MusicExpanded.TranslationVisibilityChanged -= OnGlowTranslationVisibilityChanged;
        _mediaViewModel.SetIslandGlowActive(this, false);
        _glow.Dispose();
        _presentationSnapshot = null;
        _rightHoldTimer.Stop();
        _rightHoldPointer = null;
        if (_placementEditActive)
        {
            EndPlacementEditVisuals();
        }
        _suppressedForPlacementEdit = false;
        RevokeNativeDropTarget();
        _visualPreferences.Changed -= OnSystemVisualPreferencesChanged;
        _motion.Dispose();
        _materialController.Dispose();
        MusicCompact.IdealWidthChanged -= OnMediaGeometryChanged;
        CompactPanel.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _compactPanelVisibilityToken);
        MusicCompact.SetActive(false);
        ExpandedPanel.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, _expandedPanelVisibilityToken);
        MusicExpanded.SetActive(false);
        _presentationSnapshot = null;
        WidgetsExpanded.SetActive(false);
        ClipboardExpanded.SetActive(false);
        _mediaViewModel.SetPresentationVisible(this, false);
        if (!_windowClosed) Close();
    }

    private void OnGlowMediaChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MediaViewModel.IsIslandGlowActive)) return;
        if (_lastGlowSession is { } previous && !previous.IsSameTrack(_mediaViewModel.Session))
            _glow.InvalidateFrameCapture();
        // Observe invalidations immediately, so Off→On or A→B→A in one queued
        // dispatcher turn cannot revive an obsolete transfer.
        if (_glowTransfer is { } transfer)
            transfer.State.Evaluate(MonitorId, _mediaViewModel.Session, GlowContinuationAllowed(), false, false,
                Stopwatch.GetElapsedTime(transfer.Timestamp));
        QueueGlowTargetRefresh();
    }

    private void OnGlowTranslationVisibilityChanged(object? sender, EventArgs args) => QueueGlowTargetRefresh();

    private void QueueGlowTargetRefresh()
    {
        if (_closing || _glowRefreshPending) return;
        _glowRefreshPending = true;
        if (!DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            _glowRefreshPending = false;
            if (!_closing) UpdateGlowTarget();
        })) _glowRefreshPending = false;
    }

    private void UpdateGlowTarget()
    {
        if (_closing) return;
        var preferences = _visualPreferences.Current;
        var visible = _isVisible && _isActiveWindow && _nativeWindowSafeToShow &&
            !_suppressedForPlacementEdit && !_placementEditActive;
        var eligibleSurface = visible && !_suppressedForFullscreen &&
            _presentationSnapshot?.State is OverlayState.Compact or OverlayState.Expanded;
        if (!visible || _experience.Current.Variant == IslandPresentationVariant.Idle || preferences.HighContrast || !preferences.AdvancedEffectsEnabled)
        {
            _glowTransfer = null;
            _mediaViewModel.SetIslandGlowActive(this, false);
            _glow.HideImmediately();
            return;
        }

        var values = _motion.Current.ProjectToSafeRange();
        var settings = _mediaViewModel.Settings;
        var presentation = _mediaViewModel.LyricPresentation;
        var compactLyricsVisible = MusicCompact.IsTranslationVisibleWithin(Surface) &&
            CompactPanel.Visibility == Visibility.Visible && values.CompactContent > 0.01 &&
            settings.IslandActivity.ShowLyricsInCompact;
        var expandedLyricsVisible = MusicExpanded.IsTranslationActuallyVisible &&
            ExpandedPanel.Visibility == Visibility.Visible && values.ExpandedContent > 0.01 &&
            _pageTransition.Progress(IslandPage.Music) > 0.01;
        var secondary = _mediaViewModel.SecondaryLyricText;
        var translationVisible = (compactLyricsVisible || expandedLyricsVisible) &&
            settings.Lyrics.Enabled && settings.Lyrics.SecondaryLyrics &&
            !string.IsNullOrWhiteSpace(secondary);
        var eligible = _glow.IsAvailable && LyricsGlowPolicy.IsEligible(settings.Lyrics.GlowMode,
            _mediaViewModel.IsPlaying && !string.IsNullOrWhiteSpace(_mediaViewModel.Title),
            eligibleSurface && values.Opacity > 0.01, translationVisible,
            presentation.Line?.TranslationOrigin ?? LyricsTranslationOrigin.None, secondary);
        if (_glowTransfer is { } pending)
        {
            var layoutReady = eligible || values.Opacity > 0.01 && Surface.ActualHeight > 0 &&
                (values.CompactContent >= 0.99 || values.ExpandedContent >= 0.99);
            if (pending.State.Evaluate(MonitorId, _mediaViewModel.Session, GlowContinuationAllowed(), layoutReady, eligible,
                Stopwatch.GetElapsedTime(pending.Timestamp)) is { } restored)
                _glow.RestoreVisualState(restored);
            if (layoutReady) _glowTransfer = null;
        }
        _mediaViewModel.SetIslandGlowActive(this, eligible);

        // Consume the existing selected-player loopback spectrum, never a microphone
        // or an endpoint meter. Unavailable capture leaves only the quiet baseline.
        var spectrum = _mediaViewModel.Spectrum;
        var energy = 0d;
        if (spectrum.CaptureMode == AudioCaptureMode.ProcessLoopback && spectrum.Bands.Count > 0)
        {
            var peak = 0d;
            foreach (var band in spectrum.Bands)
            {
                var level = double.IsFinite(band) ? Math.Clamp(band, 0, 1) : 0;
                energy += level * level;
                peak = Math.Max(peak, level);
            }
            // A vocal or transient in one band must not be divided away by five
            // quiet bands while the adjacent meter visibly moves.
            energy = .55 * Math.Sqrt(energy / spectrum.Bands.Count) + .45 * peak;
        }
        if (_lastGlowSession is null || !_lastGlowSession.IsSameTrack(_mediaViewModel.Session))
            _glow.InvalidateFrameCapture();
        _glow.SetTarget(eligible, energy, _mediaViewModel.IsReducedMotion || preferences.ReducedMotion,
            spectrum.CaptureMode == AudioCaptureMode.ProcessLoopback ? spectrum.Bands : null, settings.Lyrics.SimplifiedGlow);
        _lastGlowSession = _mediaViewModel.Session;
    }

    private void OnMediaGeometryChanged(object? sender, EventArgs args)
    {
        // This synchronous preparation is already followed by fresh geometry reads.
        // Existing queued invalidations still own their callback and remain pending.
        if (_closing || _preparingMediaGeometry || _mediaGeometryRefreshPending || _presentationSnapshot is null) return;
        _mediaGeometryRefreshPending = true;
        DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            _mediaGeometryRefreshPending = false;
            if (_presentationSnapshot is not { } snapshot) return;
            ApplySnapshot(snapshot, _isActiveWindow, _isActiveWindow, _viewModel.FileDragWakeMode, _viewModel.GetOverlayPlacement(_monitor.Id));
        });
    }

    private void OnCompactPanelVisibilityChanged(DependencyObject sender, DependencyProperty property) =>
        MusicCompact.SetActive(!_closing && CompactPanel.Visibility == Visibility.Visible);

    private void OnExpandedPanelVisibilityChanged(DependencyObject sender, DependencyProperty property) =>
        MusicExpanded.SetActive(!_closing && ExpandedPanel.Visibility == Visibility.Visible);

    private void EnsureVisualHostShown(bool allowActivation)
    {
        if (!_nativeWindowSafeToShow && !TryRecoverNativeSurface())
        {
            _logger.LogError(
                "Skipped showing overlay HWND {WindowHandle} on monitor {MonitorId} because native borderless setup failed.",
                _windowHandle,
                _monitor.Id);
            return;
        }

        if (!PositionFixedHost())
        {
            _logger.LogError(
                "Skipped showing overlay HWND {WindowHandle} on monitor {MonitorId} because its current client geometry is unsafe.",
                _windowHandle,
                _monitor.Id);
            HideForNativeFailure();
            return;
        }

        EnsureNativeDropTargetRegistered();
        var noActivate = !allowActivation;
        if (_noActivateApplied != noActivate &&
            !OverlayWindowInterop.SetNoActivate(_windowHandle, noActivate, out var noActivateFailure))
        {
            LogNativeFailure(noActivateFailure);
            HideForNativeFailure();
            return;
        }
        _noActivateApplied = noActivate;
        if (_isVisible)
        {
            MaintainFullscreenVisibility();
            return;
        }

        var failuresBeforeFrame = RegionFailureCount;
        // This is pre-show geometry, not a decision to hide the new surface.
        // Keep its staged handoff until real post-show visibility/layout is known.
        if (!ApplyMotionFrame(_motion.Current, updateGlowTarget: false))
        {
            return;
        }
        if (RegionFailureCount != failuresBeforeFrame)
        {
            return;
        }

        if (_nativeWindowShown)
        {
            _isVisible = true;
        }
        else if (OverlayWindowInterop.ShowNoActivateAndTopmost(_windowHandle, out var firstShowFailure))
        {
            _isVisible = true;
            _nativeWindowShown = true;
        }
        else
        {
            LogNativeFailure(firstShowFailure);
            HideForNativeFailure();
        }
    }

    internal bool NeedsFullscreenPresentationRecovery =>
        !_closing && _forceFullscreenPresentation && _isActiveWindow && !_nativeWindowSafeToShow &&
        !_suppressedForPlacementEdit && !_placementEditActive &&
        FullscreenOverlayPolicy.Allows(_mediaViewModel.Settings.IslandAppearance.ForceShowOverFullscreen, true);

    // Called by the low-frequency fullscreen watcher, not by the animation loop.
    // Reassert only a safe, visible surface; never activate or expose an empty host.
    internal void MaintainFullscreenVisibility()
    {
        if (_closing || !_isActiveWindow || !_isVisible ||
            !_nativeWindowSafeToShow || _suppressedForFullscreen || _suppressedForPlacementEdit)
            return;

        if (!OverlayWindowInterop.MaintainTopmostNoActivate(_windowHandle, out var failure))
        {
            // Failure to win the Z order is not a reason to hide the island. The
            // next foreground/timer event can retry within normal Win32 rules.
            var now = Environment.TickCount64;
            if (_lastTopmostFailureLog == 0 || now - _lastTopmostFailureLog >= 10_000)
            {
                _lastTopmostFailureLog = now;
                LogNativeFailure(failure);
            }
        }
    }

    private void HideImmediately()
    {
        var fileRevision = _presentationSnapshot?.Revision;
        var hideGeneration = _experience.HideGeneration;
        var traceHide = _islandTraceEnabled && (_isVisible || _islandTraceDismissalActive);
        if (traceHide) TraceIslandEvent("hide-immediate-begin");
        StopAnimationFrames();
        _glowTransfer = null;
        _presentedState = OverlayState.Hidden;
        _mediaViewModel.SetIslandGlowActive(this, false);
        _glow.HideImmediately();
        WidgetsExpanded.SetActive(false);
        ClipboardExpanded.SetActive(false);
        _mediaViewModel.SetPresentationVisible(this, false);
        CompactPanel.Visibility = Visibility.Collapsed;
        DragPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        if (!_nativeRegionController.ApplyEmpty(out var emptyRegionFailure))
        {
            LogNativeFailure(emptyRegionFailure);
            _nativeWindowSafeToShow = false;
        }
        // Keep the HWND alive with an empty region during normal Hidden transitions. The empty
        // region is zero-pixel and not discoverable by WindowFromPoint, while avoiding repeated
        // native show/hide allocations. A real native failure still uses HideForNativeFailure.
        _motion.SnapTo(OverlayMotionValues.Hidden);
        CompleteMotionWaiters();
        _isVisible = false;
        _hideWhenSettled = false;
        _visualPhase = OverlayVisualPhase.Invisible;
        if (traceHide) TraceIslandEvent("native-region-empty-immediate");
        _islandTraceDismissalActive = false;
        // Safe/direct hiding has already released the visible region. It must
        // also settle file ownership, even when the island projected it to Hidden.
        if (_isActiveWindow) _experience.CompleteDismissal(hideGeneration);
        CompleteFileDismissal(fileRevision);
    }

    private bool TryRecoverNativeSurface()
    {
        if (_closing) return false;
        var now = Environment.TickCount64;
        if (_lastNativeRecoveryAttempt != 0 && now - _lastNativeRecoveryAttempt < 1000) return false;
        _lastNativeRecoveryAttempt = now;
        TraceIslandEvent("native-hide-recovery");
        if (!OverlayWindowInterop.Hide(_windowHandle, out var hideFailure))
        { LogNativeFailure(hideFailure); return false; }
        var configuration = OverlayWindowInterop.ConfigureVisualWindow(_windowHandle, _supportsModernDwmAttributes);
        foreach (var failure in configuration.Failures) LogNativeFailure(failure);
        if (!configuration.IsSafeToShow || !PositionFixedHost()) return false;
        if (!_nativeRegionController.ApplyEmpty(out var regionFailure))
        { LogNativeFailure(regionFailure); return false; }
        _noActivateApplied = null;
        _nativeWindowShown = false;
        _nativeWindowSafeToShow = true;
        return true;
    }

    private void HideForNativeFailure()
    {
        var fileRevision = _presentationSnapshot?.Revision;
        var hideGeneration = _experience.HideGeneration;
        StopAnimationFrames();
        _glowTransfer = null;
        _presentedState = OverlayState.Hidden;
        _mediaViewModel.SetIslandGlowActive(this, false);
        _glow.HideImmediately();
        WidgetsExpanded.SetActive(false);
        ClipboardExpanded.SetActive(false);
        _mediaViewModel.SetPresentationVisible(this, false);
        _nativeWindowSafeToShow = false;
        CompactPanel.Visibility = Visibility.Collapsed;
        DragPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        _motion.SnapTo(OverlayMotionValues.Hidden);
        CompleteMotionWaiters();
        _hideWhenSettled = false;
        if (!_nativeRegionController.ApplyEmpty(out var emptyRegionFailure))
        {
            LogNativeFailure(emptyRegionFailure);
        }
        TraceIslandEvent("native-hide-safety");
        if (!OverlayWindowInterop.Hide(_windowHandle, out var hideFailure))
        {
            LogNativeFailure(hideFailure);
        }

        RevokeNativeDropTarget();
        _nativeWindowShown = false;
        _isVisible = false;
        _visualPhase = OverlayVisualPhase.Invisible;
        _islandTraceDismissalActive = false;
        if (_isActiveWindow) _experience.CompleteDismissal(hideGeneration);
        CompleteFileDismissal(fileRevision);
    }

    private void BeginFullscreenSuppression(OverlaySnapshot snapshot, FileDragWakeMode wakeMode)
    {
        ResetMotionBlur();
        _glowTransfer = null;
        WidgetsExpanded.SetActive(false);
        ClipboardExpanded.SetActive(false);
        _mediaViewModel.SetPresentationVisible(this, false);
        if (!_suppressedForFullscreen)
        {
            _logger.LogInformation(
                "A real user full-screen window suppressed the passive overlay on monitor {MonitorId}.",
                _monitor.Id);
        }

        if (!_isVisible)
        {
            _suppressedForFullscreen = true;
            return;
        }

        _suppressedForFullscreen = true;
        _hideWhenSettled = true;
        var hiddenTarget = CreateMotionTarget(
            OverlayState.Hidden,
            _resolvedPlacement.SurfaceTopOffsetDips);
        PrepareContentForTarget(hiddenTarget);
        _motion.SetTarget(hiddenTarget, IsReducedMotion());
        StartAnimationFrames();
    }

    private bool PositionFixedHost()
    {
        var width = ToPixels(HostWidth);
        var height = ToPixels(HostHeight);
        var left = _resolvedPlacement.HostLeftPixels;
        var top = _resolvedPlacement.HostTopPixels;
        if (_positionedHostWidthPixels == width &&
            _positionedHostHeightPixels == height &&
            _positionedHostLeftPixels == left &&
            _positionedHostTopPixels == top &&
            OverlayWindowInterop.TryGetClientSize(_windowHandle, out var cachedWidth, out var cachedHeight) &&
            cachedWidth == width &&
            cachedHeight == height)
        {
            return true;
        }

        // Host/DPI changes invalidate the continuous physical pose history.
        ResetMotionBlur();
        // The animated HRGN is expressed in client coordinates. ResizeClient keeps that
        // coordinate space exact even when Windows reports a presenter-specific outer frame;
        // Move uses independent screen coordinates for the host's origin. During a display/DPI
        // transition either AppWindow call can temporarily reject the stale HWND/monitor
        // geometry; treat that frame as unsafe and let the topology refresh retry it.
        try
        {
            AppWindow.ResizeClient(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(left, top));
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(
                exception,
                "Overlay HWND geometry update was rejected on monitor {MonitorId}; the frame will remain hidden until the next safe retry.",
                _monitor.Id);
            return false;
        }
        var matches = OverlayWindowInterop.TryGetClientSize(_windowHandle, out var actualWidth, out var actualHeight) &&
                      actualWidth == width && actualHeight == height;
        if (!matches)
        {
            _logger.LogWarning(
                "Overlay HWND/client geometry mismatch on monitor {MonitorId}: expected {ExpectedWidth}x{ExpectedHeight}px, actual {ActualWidth}x{ActualHeight}px, scale {Scale:F2}, OS build {OperatingSystemBuild}.",
                _monitor.Id,
                width,
                height,
                actualWidth,
                actualHeight,
                _monitor.Scale,
                _operatingSystemBuild);
        }

        if (matches)
        {
            _positionedHostWidthPixels = width;
            _positionedHostHeightPixels = height;
            _positionedHostLeftPixels = left;
            _positionedHostTopPixels = top;
            _glow.RefreshPosition();
        }

        return matches;
    }

    private OverlayResolvedPlacement ResolvePlacement(
        FileDragWakeMode wakeMode,
        OverlayMonitorPlacement placement) =>
        OverlayPlacementPolicy.Resolve(
            new OverlayPlacementRequest(
                _monitor.EffectiveWorkLeft,
                _monitor.EffectiveWorkTop,
                _monitor.EffectiveWorkWidth,
                _monitor.EffectiveWorkHeight,
                _monitor.Scale,
                wakeMode,
                HostContentScale, HostWidth, Math.Max(560 * ExpandedScale, _compactSurfaceWidth)),
            placement);

    internal void RefreshAnimationRefreshRate() =>
        _framePacer.SetRefreshRate(_monitorLayout.GetCachedRefreshRateHz(MonitorId));

    private void StartAnimationFrames(bool preferDisplayCadence = false)
    {
        if (_closing) return;
        _motionSettled ??= new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var useDisplayCadence = preferDisplayCadence && !IsReducedMotion() && _motion.IsGeometryAnimating;
        if (_hasFrameSubscription)
        {
            if (useDisplayCadence && !_displayAnimationFrames) UseDisplayAnimationFrames();
            return;
        }

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        _hasFrameSubscription = true;
        if (useDisplayCadence)
        {
            UseDisplayAnimationFrames();
            return;
        }
        UseTimerAnimationFrames();
    }

    private void UseDisplayAnimationFrames()
    {
        if (_displayAnimationFrames) return;
        _animationTimer.Stop();
        RefreshAnimationRefreshRate();
        _framePacer.Reset();
        _displayAnimationFrames = true;
        CompositionTarget.Rendering += OnDisplayAnimationFrame;
    }

    private void UseTimerAnimationFrames()
    {
        ResetMotionBlur();
        if (_displayAnimationFrames)
        {
            CompositionTarget.Rendering -= OnDisplayAnimationFrame;
            _displayAnimationFrames = false;
            _framePacer.Reset();
        }
        _animationTimer.Interval = AnimationTimerInterval;
        if (!_animationTimer.IsRunning)
        {
            _animationTimer.Start();
        }
    }

    private void StopAnimationFrames()
    {
        ResetMotionBlur();
        _animationTimer?.Stop();
        if (_displayAnimationFrames)
        {
            CompositionTarget.Rendering -= OnDisplayAnimationFrame;
            _displayAnimationFrames = false;
        }
        _framePacer.Reset();
        _hasFrameSubscription = false;
    }

    internal Task WaitForMotionSettledAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasFrameSubscription && !_motion.IsAnimating && !_pageTransition.IsAnimating)
        {
            return Task.CompletedTask;
        }

        _motionSettled ??= new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _motionSettled.Task.WaitAsync(cancellationToken);
    }

    private void CompleteMotionWaiters()
    {
        var settled = Interlocked.Exchange(ref _motionSettled, null);
        settled?.TrySetResult(null);
    }

    private void OnTimerAnimationFrame(DispatcherQueueTimer sender, object args)
    {
        if (!_displayAnimationFrames) OnAnimationFrame(sender, args);
    }

    private void OnDisplayAnimationFrame(object? sender, object args)
    {
        if (_displayAnimationFrames) OnAnimationFrame(sender, args);
    }

    private void OnAnimationFrame(object? sender, object args)
    {
        if (_closing || !_hasFrameSubscription) return;
        try { ApplyAnimationFrame(); }
        catch
        {
            StopAnimationFrames();
            throw;
        }
    }

    private void ApplyAnimationFrame()
    {
        var now = Stopwatch.GetTimestamp();
        if (_displayAnimationFrames && !_framePacer.ShouldRender(now)) return;
        var elapsed = Stopwatch.GetElapsedTime(_lastFrameTimestamp, now);
        _lastFrameTimestamp = now;
        var previousBlurGeometry = ProjectMotionBlurGeometry(_motion.Current);
        _motion.Step(elapsed);
        var blurFrame = _motionBlurPolicy.Update(
            previousBlurGeometry, ProjectMotionBlurGeometry(_motion.Current), elapsed, _motionBlurPhase,
            enabled: _motionBlurEnabled && _displayAnimationFrames,
            visible: _isVisible && !_hideWhenSettled,
            animated: _motion.IsGeometryAnimating, reducedMotion: IsReducedMotion());
        _pageTransition.Step(elapsed);
        ApplyPageTransition();

        // A display-rate geometry driver does not raise the cadence of media/glow
        // eligibility polling. Its existing independent glow timer is unchanged.
        var refreshGlowTarget = !_displayAnimationFrames ||
            Stopwatch.GetElapsedTime(_lastAnimationGlowRefresh, now) >= AnimationTimerInterval ||
            (!_motion.IsAnimating && !_pageTransition.IsAnimating);
        if (refreshGlowTarget) _lastAnimationGlowRefresh = now;
        if (!ApplyMotionFrame(_motion.Current, updateGlowTarget: refreshGlowTarget))
        {
            StopAnimationFrames();
            return;
        }
        TraceIslandDismissalFrame();

        // Publish the shutter only after XAML, the AA mask and native region succeeded.
        _materialController.SetMotion(blurFrame);

        if (_motion.IsAnimating || _pageTransition.IsAnimating)
        {
            if (_displayAnimationFrames && (!_motion.IsGeometryAnimating || IsReducedMotion()))
                UseTimerAnimationFrames();
            return;
        }

        StopAnimationFrames();
        if (_hideWhenSettled)
        {
            if (_presentedState != OverlayState.Dismissing ||
                _experience.Current.State != OverlayState.Dismissing || _dismissalGeneration != _experience.HideGeneration)
            {
                // A newer display reason owns presentation now. An obsolete frame
                // cannot release its region; retarget from current authority instead.
                _hideWhenSettled = false;
                ApplySnapshot(_viewModel.Snapshot with { State = _experience.Current.State }, _isActiveWindow, true,
                    _viewModel.FileDragWakeMode, _viewModel.GetOverlayPlacement(_monitor.Id));
                return;
            }
            var fileRevision = _presentationSnapshot?.Revision;
            var current = _motion.Current.ProjectToSafeRange();
            var target = _motion.Target.ProjectToSafeRange();
            if (current.Opacity > 0.01 ||
                Math.Abs(current.Width - target.Width) > 4 ||
                Math.Abs(current.Height - target.Height) > 4)
            {
                StartAnimationFrames();
                return;
            }
            TraceIslandEvent("dismiss-settled");
            _hideWhenSettled = false;
            _motion.CompleteVisualAnimations();
            CollapseInvisibleContent(_motion.Current);
            if (!_nativeRegionController.ApplyEmpty(out var emptyRegionFailure))
            {
                LogNativeFailure(emptyRegionFailure);
                _nativeWindowSafeToShow = false;
            }
            // The empty HRGN keeps this zero-pixel state out of WindowFromPoint while the HWND
            // remains allocated for reuse.
            _isVisible = false;
            _visualPhase = OverlayVisualPhase.Invisible;
            TraceIslandEvent("native-region-empty-settled");
            _islandTraceDismissalActive = false;
            _experience.CompleteDismissal(_dismissalGeneration);
            CompleteFileDismissal(fileRevision);
            CompleteMotionWaiters();
            return;
        }

        _visualPhase = OverlayVisualPhase.Visible;

        if (!_isActiveWindow)
        {
            return;
        }

        // Files finish independently of island visibility. Music, residence or
        // a pending hide can keep this surface Compact after the last file leaves.
        CompleteFileDismissal(_presentationSnapshot?.Revision);
        CompleteMotionWaiters();
    }

    private void CompleteFileDismissal(long? revision)
    {
        if (revision is { } expected && _viewModel.Snapshot.Revision == expected &&
            _experience.CanCompleteFileDismissal(expected))
            _viewModel.CompleteDismissal();
    }

    private void TraceIslandPresentation(OverlayState requestedState)
    {
        if (!_islandTraceEnabled) return;
        var experience = _experience.Current;
        var signature = $"{requestedState}|{_presentedState}|{experience.State}|{experience.Variant}|" +
            $"{_mediaViewModel.Settings.IslandAppearance.ShowLogoWhenIdle}|{EmptyWakeLogo.Visibility}|" +
            $"{FileCompactContent.Visibility}|{_experience.HideGeneration}|{_viewModel.Snapshot.State}|{_viewModel.Snapshot.Revision}";
        if (_islandTracePresentation != signature)
        {
            _islandTracePresentation = signature;
            TraceIslandEvent("presentation");
        }
        if (_hideWhenSettled)
        {
            if (_islandTraceDismissalActive && _islandTraceDismissalGeneration == _dismissalGeneration) return;
            _islandTraceDismissalActive = true;
            _islandTraceDismissalGeneration = _dismissalGeneration;
            _islandTraceOpacityQuarter = 4;
            TraceIslandEvent("dismiss-start");
        }
        else if (_islandTraceDismissalActive)
        {
            TraceIslandEvent("dismiss-cancelled");
            _islandTraceDismissalActive = false;
        }
    }

    private void TraceIslandDismissalFrame()
    {
        if (!_islandTraceEnabled || !_islandTraceDismissalActive || !_hideWhenSettled) return;
        var opacity = Math.Clamp(_motion.Current.Opacity, 0, 1);
        var quarter = opacity <= 0.01 ? 0 : (int)Math.Ceiling(opacity * 4);
        if (quarter >= _islandTraceOpacityQuarter) return;
        _islandTraceOpacityQuarter = quarter;
        // Only actual successfully applied frames are sampled. Reduced motion can
        // skip quarters; do not invent rendered intermediate frames in the log.
        TraceIslandEvent("dismiss-frame");
    }

    private void TraceIslandEvent(string traceEvent)
    {
        if (!_islandTraceEnabled) return;
        var values = _motion.Current.ProjectToSafeRange();
        var compactVisible = _isVisible && CompactPanel.Visibility == Visibility.Visible &&
            values.Opacity > 0.001 && values.CompactContent > 0.001;
        _logger.LogInformation(
            "IslandTrace event={TraceEvent} sequence={Sequence} ticks={Ticks} monitor={MonitorId} " +
            "generation={Generation} dismissalGeneration={DismissalGeneration} state={State} presented={Presented} " +
            "variant={Variant} phase={Phase} visible={Visible} nativeShown={NativeShown} " +
            "fileState={FileState} fileRevision={FileRevision} fileCount={FileCount} " +
            "idleLogoEnabled={IdleLogoEnabled} idleLogo={IdleLogo} fileLogo={FileLogo} fileContent={FileContent} " +
            "idleLogoVisible={IdleLogoVisible} fileLogoVisible={FileLogoVisible} " +
            "opacity={Opacity:F4} compactOpacity={CompactOpacity:F4} expandedOpacity={ExpandedOpacity:F4} " +
            "quarter={Quarter} reducedMotion={ReducedMotion}",
            traceEvent, ++_islandTraceSequence, Stopwatch.GetTimestamp(), _monitor.Id,
            _experience.HideGeneration, _islandTraceDismissalGeneration, _experience.Current.State, _presentedState,
            _experience.Current.Variant, _visualPhase, _isVisible, _nativeWindowShown,
            _viewModel.Snapshot.State, _viewModel.Snapshot.Revision, _viewModel.Snapshot.TemporaryItemCount,
            _mediaViewModel.Settings.IslandAppearance.ShowLogoWhenIdle, EmptyWakeLogo.Visibility,
            FileLogo.Visibility, FileCompactContent.Visibility,
            compactVisible && EmptyWakeLogo.Visibility == Visibility.Visible,
            compactVisible && FileCompactContent.Visibility == Visibility.Visible && FileLogo.Visibility == Visibility.Visible,
            values.Opacity, values.CompactContent,
            values.ExpandedContent, _islandTraceOpacityQuarter, IsReducedMotion());
    }

    private void OnSystemVisualPreferencesChanged(object? sender, EventArgs args)
    {
        if (_closing) return;
        ResetMotionBlur();
        _materialController.Apply(_visualPreferences.Resolve(_viewModel.MotionPreference));
        UpdateGlowTarget();
        if (_presentationSnapshot is { State: not OverlayState.Hidden } snapshot)
        {
            ApplySnapshot(
                snapshot,
                _isActiveWindow,
                _isActiveWindow,
                _viewModel.FileDragWakeMode,
                _viewModel.GetOverlayPlacement(_monitor.Id));
        }
    }

    private void ApplyPageTransition()
    {
        ApplyPage(FilesExpanded, IslandPage.Files);
        ApplyPage(MusicExpanded, IslandPage.Music);
        ApplyPage(WidgetsExpanded, IslandPage.Widgets);
        ApplyPage(ClipboardExpanded, IslandPage.Clipboard);
    }

    private void ApplyPage(FrameworkElement element, IslandPage page)
    {
        var progress = _pageTransition.Progress(page);
        element.Opacity = progress;
        element.Visibility = progress > 0 || page == _pageTransition.Target ? Visibility.Visible : Visibility.Collapsed;
        element.IsHitTestVisible = page == _pageTransition.Target;
        if (!_pageOffsets.TryGetValue(element, out var offset))
        {
            offset = new TranslateTransform(); _pageOffsets[element] = offset;
            element.RenderTransform = offset;
        }
        offset.Y = _pageReducedMotion ? 0 : 4 * (1 - progress);
    }

    private bool ApplyMotionFrame(OverlayMotionValues values, bool updateGlowTarget = true)
    {
        values = ProjectMotionToHostSurface(values.ProjectToSafeRange());
        _compositionAnimator.ApplyMotion(values);
        var geometry = OverlayFrameGeometry.Create(values, HostWidth, _monitor.Scale);
        var signature = geometry.Region;
        Surface.Width = geometry.WidthDips;
        Surface.Height = geometry.HeightDips;
        var radius = new CornerRadius(geometry.TopRadiusDips,
            geometry.TopRadiusDips, geometry.BottomRadiusDips, geometry.BottomRadiusDips);
        Surface.CornerRadius = new CornerRadius(0);
        _materialController.SetCornerRadius(radius);
        InteractionTintOverlay.CornerRadius = radius;
        SurfaceTransform.ScaleX = geometry.SurfaceScale;
        SurfaceTransform.ScaleY = geometry.SurfaceScale;
        CollapseInvisibleContent(values);

        // Width/Height setters invalidate arrange; the native region and mask otherwise
        // advance ahead of the allocated backdrop during a resize frame. Resolve only
        // a changed allocation before publishing that frame's physical coverage.
        if (Root.ActualWidth > 0 &&
            (Math.Abs(Surface.ActualWidth - geometry.WidthDips) > 0.01 ||
             Math.Abs(Surface.ActualHeight - geometry.HeightDips) > 0.01))
            Surface.UpdateLayout();
        var rootWidth = Root.ActualWidth > 0 ? Root.ActualWidth : HostWidth;
        SurfaceTransform.TranslateX = geometry.TranslationXDips(rootWidth);
        SurfaceTransform.TranslateY = geometry.TranslationYDips;
        _materialController.SetGeometry(signature);

        var width = signature.WidthPixels;
        var height = signature.HeightPixels;
        var left = signature.LeftPixels;
        var top = signature.TopPixels;
        var hostWidth = ToPixels(HostWidth);
        var hostHeight = ToPixels(HostHeight);
        if (left < 0 || top < 0 || width <= 0 || height <= 0 ||
            left > hostWidth - width || top > hostHeight - height)
        {
            Interlocked.Increment(ref _regionFailureCount);
            var geometryFailure = new OverlayNativeFailure(
                "Validate overlay region against fixed client surface",
                Critical: true,
                Win32Error: 87);
            LogNativeFailure(geometryFailure);
            HideForNativeFailure();
            _logger.LogError(
                "The overlay frame {Width}x{Height}+{Left}+{Top} exceeded the fixed client surface {HostWidth}x{HostHeight} on monitor {MonitorId}; the unsafe frame was hidden.",
                width,
                height,
                left,
                top,
                hostWidth,
                hostHeight,
                _monitor.Id);
            return false;
        }

        if (values.Opacity <= 0.001 &&
            (!_hideWhenSettled || !_motion.IsAnimating && !_pageTransition.IsAnimating))
        {
            if (!_nativeRegionController.ApplyEmpty(out var emptyRegionFailure))
            {
                Interlocked.Increment(ref _regionFailureCount);
                LogNativeFailure(emptyRegionFailure);
                HideForNativeFailure();
                return false;
            }

            _glow.SetGeometry(left, top, width, height,
                signature.TopRadiusPixels, signature.BottomRadiusPixels, 0);
            if (updateGlowTarget) UpdateGlowTarget();
            return true;
        }

        if (!_nativeRegionController.Apply(
                signature,
                out var regionFailure))
        {
            Interlocked.Increment(ref _regionFailureCount);
            LogNativeFailure(regionFailure);
            HideForNativeFailure();
            _logger.LogError(
                "The overlay HRGN could not be applied for monitor {MonitorId}; the unsafe frame was hidden.",
                _monitor.Id);
            return false;
        }

        // Apply the body's region before presenting its underlapping light, so a
        // geometry transition cannot expose the next glow contour above the old body.
        _glow.SetGeometry(left, top, width, height,
            signature.TopRadiusPixels, signature.BottomRadiusPixels, values.Opacity);
        if (updateGlowTarget) UpdateGlowTarget();
        return true;
    }

    private void ResetMotionBlur()
    {
        _motionBlurPhase = IslandMotionPhase.Stable;
        _motionBlurPolicy.Reset();
        _materialController?.SetMotion(IslandMotionBlurFrame.None);
    }

    private IslandMotionGeometry ProjectMotionBlurGeometry(OverlayMotionValues values)
    {
        values = ProjectMotionToHostSurface(values.ProjectToSafeRange());
        var scale = Math.Min(values.DropTargetScale, 1);
        var width = values.Width * scale * _monitor.Scale;
        var height = values.Height * scale * _monitor.Scale;
        return new IslandMotionGeometry(
            (HostWidth * _monitor.Scale - width) / 2,
            (values.TopOffset + values.Height * (1 - scale) / 2) * _monitor.Scale,
            width, height);
    }

    private OverlayMotionValues ProjectMotionToHostSurface(OverlayMotionValues values)
    {
        // Spring overshoot is expected during reversals, but the native HRGN must remain inside
        // the fixed client surface on every frame. Keep a small physical-pixel safety margin and
        // project the rendered frame before applying WinUI dimensions or Win32 geometry.
        var marginDips = 2d / _monitor.Scale;
        var scale = Math.Min(values.DropTargetScale, 1);
        var topOffset = Math.Clamp(
            values.TopOffset,
            0,
            Math.Max(0, HostHeight - marginDips));
        var availableWidth = Math.Min(HostWidth, _monitor.EffectiveWorkWidth / _monitor.Scale);
        var availableHeight = Math.Min(HostHeight,
            (_monitor.EffectiveWorkTop + _monitor.EffectiveWorkHeight - _resolvedPlacement.HostTopPixels) / _monitor.Scale);
        var width = Math.Min(
            values.Width,
            Math.Max(OverlayMotionValues.MinimumDimension, (availableWidth - marginDips * 2) / scale));
        var height = Math.Min(
            values.Height,
            Math.Max(
                OverlayMotionValues.MinimumDimension,
                2 * (availableHeight - topOffset - marginDips) / (1 + scale)));

        return (values with
        {
            Width = width,
            Height = height,
            TopOffset = topOffset,
            DropTargetScale = scale,
        }).ProjectToSafeRange();
    }

    private void PrepareContentForTarget(OverlayMotionValues target)
    {
        if (target.CompactContent > 0)
        {
            CompactPanel.Visibility = Visibility.Visible;
            CompactPanel.IsHitTestVisible = true;
        }
        else
        {
            CompactPanel.IsHitTestVisible = false;
        }
        // The initial XAML panel can already be Visible, so assigning Visible
        // above need not raise a property callback on the first presentation.
        OnCompactPanelVisibilityChanged(CompactPanel, UIElement.VisibilityProperty);

        if (target.DragContent > 0)
        {
            DragPanel.Visibility = Visibility.Visible;
            DragPanel.IsHitTestVisible = true;
        }
        else
        {
            DragPanel.IsHitTestVisible = false;
        }

        if (target.ExpandedContent > 0)
        {
            ExpandedPanel.Visibility = Visibility.Visible;
            ExpandedPanel.IsHitTestVisible = true;
        }
        else
        {
            ExpandedPanel.IsHitTestVisible = false;
        }
        OnExpandedPanelVisibilityChanged(ExpandedPanel, UIElement.VisibilityProperty);
    }

    private void CollapseInvisibleContent(OverlayMotionValues values)
    {
        if (values.CompactContent <= 0.001 && _motion.Target.CompactContent == 0)
        {
            CompactPanel.Visibility = Visibility.Collapsed;
        }

        if (values.DragContent <= 0.001 && _motion.Target.DragContent == 0)
        {
            DragPanel.Visibility = Visibility.Collapsed;
        }

        if (values.ExpandedContent <= 0.001 && _motion.Target.ExpandedContent == 0)
        {
            ExpandedPanel.Visibility = Visibility.Collapsed;
        }
    }

    private bool IsReducedMotion() => _visualPreferences.IsReducedMotion(_viewModel.MotionPreference);

    private int ToPixels(double dips) => Math.Max(0, (int)Math.Round(dips * _monitor.Scale));

    private OverlayMotionValues CreateMotionTarget(OverlayState state, double topOffset)
    {
        var geometry = DropSpace.Core.Island.IslandGeometry.ForFiles(state);
        if (state == OverlayState.Expanded)
            geometry = geometry with { Width = geometry.Width * ExpandedScale, Height = geometry.Height * ExpandedScale, Radius = geometry.Radius * ExpandedScale };
        return state switch
        {
            OverlayState.DragApproaching or OverlayState.DragReady => Create(geometry.Width, geometry.Height, topOffset, geometry.Radius, 0, 1, 0),
            OverlayState.Compact => Create(geometry.Width, geometry.Height, topOffset, geometry.Radius, 1, 0, 0),
            OverlayState.Expanded => Create(geometry.Width, geometry.Height, topOffset, geometry.Radius, 0, 0, 1),
            OverlayState.Dismissing or OverlayState.Hidden => new OverlayMotionValues(
                geometry.Width,
                geometry.Height,
                topOffset,
                geometry.Radius,
                geometry.Radius,
                0,
                0,
                0,
                0,
                0.92,
                0),
            _ => OverlayMotionValues.Hidden,
        };
    }

    private static OverlayMotionValues Create(
        double width,
        double height,
        double topOffset,
        double radius,
        double compactContent,
        double dragContent,
        double expandedContent) =>
        new(
            width,
            height,
            topOffset,
            radius,
            radius,
            1,
            compactContent,
            dragContent,
            expandedContent,
            1,
            1);

    private void EnsureNativeDropTargetRegistered()
    {
        _nativeDropTarget ??= _dragDropService.RegisterVisualTarget(
            _windowHandle,
            _monitor.Id,
            _visualDragCallbacks);
    }

    internal bool ProbeIdleTopEdgePassThrough()
    {
        var x = _monitor.Left + _monitor.Width / 2;
        var y = _monitor.Top + 2;
        var probe = OverlayWindowInterop.ProbeWindowAtPoint(_windowHandle, x, y);
        _logger.LogInformation(
            "Idle top-edge pass-through probe on monitor {MonitorId}: WindowFromPoint {DiscoveredWindow}, class {WindowClassName}, ownedByDropSpace={Owned}.",
            _monitor.Id,
            probe.DiscoveredWindow,
            probe.WindowClassName,
            probe.IsRootOrDescendant);
        return !probe.IsRootOrDescendant;
    }

    private void RevokeNativeDropTarget()
    {
        var target = Interlocked.Exchange(ref _nativeDropTarget, null);
        target?.Dispose();
    }

    public OverlayCustomPlacement GetProjectedPlacement(OverlayMonitorPlacement placement)
    {
        var resolved = ResolvePlacement(_viewModel.FileDragWakeMode, placement);
        return OverlayPlacementPolicy.ProjectResolvedPlacement(
            resolved,
            _monitor.EffectiveWorkLeft,
            _monitor.EffectiveWorkTop,
            _monitor.Scale,
            HostContentScale, HostWidth);
    }

    public void SuspendForPlacementEdit()
    {
        _suppressedForPlacementEdit = true;
        _isActiveWindow = false;
        EndPlacementEditVisuals();
        HideImmediately();
    }

    public void ResumeAfterPlacementEdit() => _suppressedForPlacementEdit = false;

    public void BeginPlacementEdit(OverlayCustomPlacement savedOriginal, OverlayCustomPlacement projectedStart)
    {
        ResetMotionBlur();
        _suppressedForPlacementEdit = false;
        _placementEdit.Arm(savedOriginal, projectedStart);
        _placementEditActive = true;
        _isActiveWindow = true;
        _suppressedForFullscreen = false;
        _hideWhenSettled = false;
        _resolvedPlacement = ResolvePlacement(
            _viewModel.FileDragWakeMode,
            new OverlayMonitorPlacement(OverlayPlacementMode.Custom, projectedStart.X, projectedStart.Y));
        if (!PositionFixedHost())
        {
            _logger.LogError(
                "Placement edit kept overlay HWND {WindowHandle} hidden on monitor {MonitorId} because its client geometry is unsafe.",
                _windowHandle,
                _monitor.Id);
            HideForNativeFailure();
            return;
        }
        RevokeNativeDropTarget();
        PlacementEditSurface.Visibility = Visibility.Visible;
        PlacementEditSurface.IsHitTestVisible = true;
        PlacementConfirmActions.Visibility = Visibility.Collapsed;
        CancelPlacementButton.Content = _strings.Get("CommonCancel");
        var editTarget = Create(OverlayPlacementEditSession.EditorWidth, OverlayPlacementEditSession.EditorHeight, 0, 24, 0, 0, 0);
        PrepareContentForTarget(editTarget);
        _motion.SetTarget(editTarget, IsReducedMotion());
        if (_rightHoldPointer is { } held && OverlayWindowInterop.TryGetCursorPosition(out var pointerPosition))
        {
            Surface.ReleasePointerCapture(held);
            PlacementEditSurface.CapturePointer(held);
            _placementEdit.TryBeginDrag(pointerPosition);
        }
        if (!OverlayWindowInterop.SetNoActivate(_windowHandle, true, out var noActivateFailure))
        {
            LogNativeFailure(noActivateFailure);
            HideForNativeFailure();
            return;
        }
        _noActivateApplied = true;
        if (!_isVisible)
        {
            if (!ApplyMotionFrame(_motion.Current))
            {
                return;
            }
            if (_nativeWindowShown)
            {
                _isVisible = true;
            }
            else if (OverlayWindowInterop.ShowNoActivateAndTopmost(_windowHandle, out var showFailure))
            {
                _isVisible = true;
                _nativeWindowShown = true;
            }
            else
            {
                LogNativeFailure(showFailure);
                HideForNativeFailure();
            }
        }

        StartAnimationFrames();
    }

    public void CancelPlacementEdit()
    {
        if (!_placementEditActive)
        {
            return;
        }

        _placementEdit.Cancel();
        EndPlacementEditVisuals();
        PlacementCancelled?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlacementEditPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        for (var element = args.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is Button) return;
        if (!_placementEditActive || !OverlayWindowInterop.TryGetCursorPosition(out var point) ||
            !_placementEdit.TryBeginDrag(point))
        {
            return;
        }

        PlacementEditSurface.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void OnPlacementEditPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_placementEditActive || _placementEdit.State != OverlayPlacementEditState.Dragging ||
            !OverlayWindowInterop.TryGetCursorPosition(out var point))
        {
            return;
        }

        _placementEdit.Move(point, _monitor.Scale);
        _resolvedPlacement = ResolvePlacement(
            _viewModel.FileDragWakeMode,
            new OverlayMonitorPlacement(
                OverlayPlacementMode.Custom,
                _placementEdit.Preview.X,
                _placementEdit.Preview.Y));
        if (!PositionFixedHost())
        {
            HideForNativeFailure();
            CancelPlacementEdit();
            return;
        }
        args.Handled = true;
    }

    private void OnPlacementEditPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_placementEditActive || _placementEdit.State != OverlayPlacementEditState.Dragging)
        {
            return;
        }

        if (OverlayWindowInterop.TryGetCursorPosition(out var point))
        {
            _placementEdit.Move(point, _monitor.Scale);
        }

        _placementEdit.EndDrag();
        PlacementEditSurface.ReleasePointerCapture(args.Pointer);
        PlacementConfirmActions.Visibility = Visibility.Visible;
        args.Handled = true;
    }

    private void OnConfirmPlacementClicked(object sender, RoutedEventArgs args)
    {
        if (!_placementEditActive) return;
        var committed = _placementEdit.Commit();
        EndPlacementEditVisuals();
        PlacementCommitted?.Invoke(this, new OverlayPlacementEditEventArgs(committed));
    }

    private void OnCancelPlacementClicked(object sender, RoutedEventArgs args) => CancelPlacementEdit();

    private void OnPlacementEditPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_placementEditActive && _placementEdit.State == OverlayPlacementEditState.Dragging)
        {
            _placementEdit.Cancel();
            EndPlacementEditVisuals();
            PlacementCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EndPlacementEditVisuals()
    {
        PlacementEditSurface.IsHitTestVisible = false;
        PlacementEditSurface.Visibility = Visibility.Collapsed;
        _placementEditActive = false;
    }

    private async void OnCompactClicked(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        _hideWhenSettled = false;
        try
        {
            if (_experience.Current.CompactContent == DropSpace.Core.Island.IslandContentKind.Music)
                _experience.Open(DropSpace.Core.Island.IslandPage.Music);
            else
            {
                _hideWhenSettled = false;
                _experience.Open(DropSpace.Core.Island.IslandPage.Files);
                var generation = _experience.HideGeneration;
                await _viewModel.ExpandAsync();
                if (_closing || generation != _experience.HideGeneration || !_experience.IsManuallyOpen) return;
            }
            // The await can span a display rebuild, setting change or shutdown.
            if (_closing) return;
            var settings = _mediaViewModel.Settings;
            var presentation = FullscreenOverlayPolicy.Resolve(OverlayState.Expanded,
                settings.IslandAppearance.ForceShowOverFullscreen,
                _monitorLayout.IsForegroundFullscreen(_monitor));
            if (!presentation.AllowActivation) return;
            if (!OverlayWindowInterop.SetNoActivate(_windowHandle, false, out var noActivateFailure))
            {
                LogNativeFailure(noActivateFailure);
                return;
            }
            _noActivateApplied = false;
            Activate();
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Overlay expansion failed.");
        }
    }

    private async Task LoadBrandLogoAsync()
    {
        try
        {
            using var resource = typeof(OverlayWindow).Assembly.GetManifestResourceStream("DropSpace.Brand.TransparentLogo.png")
                ?? throw new InvalidDataException("Embedded transparent logo is missing.");
            using var bytes = new MemoryStream(); resource.CopyTo(bytes);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes.ToArray()); await writer.StoreAsync(); writer.DetachStream();
            }
            stream.Seek(0);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await image.SetSourceAsync(stream);
            if (_closing) return;
            FileLogo.Source = image; EmptyWakeLogo.Source = image;
        }
        catch (Exception error) { _logger.LogWarning("Island logo decode failed ({Category}).", error.GetType().Name); }
    }

    private void OnIslandKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape || _placementEditActive) return;
        _experience.DismissNow(); args.Handled = true;
    }
    private void OnCollapseClicked(object sender, RoutedEventArgs args) { _experience.Collapse(); _viewModel.Collapse(); }

    private void OnPreviousPageClicked(object sender, RoutedEventArgs args)
    {
        if (_experience.Current.Page > DropSpace.Core.Island.IslandPage.Widgets)
            _experience.SelectPage(_experience.Current.Page - 1);
    }

    public void RefreshLanguage()
    {
        XamlResourceOverride.ApplyTree(Root);
        WidgetsExpanded.ViewModel = _widgetViewModel;
    }
    private void OnNextPageClicked(object sender, RoutedEventArgs args)
    {
        if (_experience.Current.Page < DropSpace.Core.Island.IslandPage.Clipboard)
            _experience.SelectPage(_experience.Current.Page + 1);
    }
    private async void OnWidgetSettingsRequested(object? sender, EventArgs args)
    {
        try { if (_closing) return; await _widgetViewModel.OpenSettingsAsync(); if (_closing) return; _openMainWindow(); _experience.Collapse(); _viewModel.Collapse(); }
        catch (Exception exception) { _logger.LogWarning("Widget settings navigation failed ({Category}).", exception.GetType().Name); }
    }
    private async void OnClipboardOpenMainRequested(object? sender, EventArgs args)
    {
        try { if (_closing) return; if (ClipboardExpanded.ViewModel is { } view) await view.OpenMainAsync(); if (_closing) return; _openMainWindow(); _experience.Collapse(); _viewModel.Collapse(); }
        catch (Exception exception) { _logger.LogWarning("Clipboard navigation failed ({Category}).", exception.GetType().Name); }
    }

    private void OnOpenMainWindowClicked(object sender, RoutedEventArgs args)
    {
        _openMainWindow();
        _experience.Collapse();
        _viewModel.Collapse();
    }

    private async void OnOpenItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is not { } card)
        {
            return;
        }

        try
        {
            await _viewModel.OpenAsync(card);
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Overlay open action failed.");
        }
    }

    private async void OnPinItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is not { } card)
        {
            return;
        }

        try
        {
            await _viewModel.TogglePinAsync(card);
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Overlay pin action failed.");
        }
    }

    private async void OnRemoveItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is not { } card)
        {
            return;
        }

        try
        {
            await _viewModel.RemoveAsync(card);
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Overlay remove action failed.");
        }
    }

    private async void OnPrimaryQuickActionClicked(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        if (sender is not FrameworkElement { Tag: QuickActionButtonViewModel quickAction })
        {
            return;
        }

        try
        {
            if (Root.XamlRoot is not { } xamlRoot)
            {
                _logger.LogWarning("Overlay quick action could not open its parameter surface because XamlRoot is unavailable.");
                return;
            }

            var context = await _quickActionDialog.RequestAsync(
                new ItemSelectionSnapshot([DropItemSnapshot.FromItem(quickAction.Card.Item)]),
                quickAction.ActionId,
                xamlRoot,
                _windowHandle, _windowLifetime.Token);
            if (_closing || context is null)
            {
                return;
            }

            var result = await _viewModel.ExecuteQuickActionAsync(quickAction, context);
            if (_closing) return;
            await _quickActionDialog.ShowResultAsync(result, xamlRoot, _windowLifetime.Token);
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Overlay quick action failed.");
        }
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        var cards = args.Items.OfType<ItemCardViewModel>().ToArray();
        var storageItems = cards
            .Select(card => card.DragStorageItem)
            .Where(item => item is not null)
            .Cast<Windows.Storage.IStorageItem>()
            .ToArray();
        if (storageItems.Length > 0)
        {
            args.Data.SetStorageItems(storageItems, readOnly: true);
        }
        else if (cards.Length == 1 && cards[0].Item.Text?.InlineText is { } text)
        {
            args.Data.SetText(text);
            if (cards[0].Item.Url is { NormalizedUrl: var url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                args.Data.SetWebLink(uri);
            }
        }
        else
        {
            args.Cancel = true;
            return;
        }

        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnSurfacePointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (_presentedState == OverlayState.Compact)
        {
            _motion.ApplyHover(true, IsReducedMotion());
        }
    }

    private void OnSurfacePointerExited(object sender, PointerRoutedEventArgs args)
    {
        _motion.ApplyHover(false, IsReducedMotion());
    }

    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_placementEditActive && _mediaViewModel.Settings.IslandAppearance.RightClickHoldToMove && args.GetCurrentPoint(Surface).Properties.IsRightButtonPressed)
        {
            _rightHoldPointer = args.Pointer;
            Surface.CapturePointer(args.Pointer);
            _rightHoldTimer.Start();
            args.Handled = true;
            return;
        }
        if (_presentedState == OverlayState.Compact)
        {
            _motion.ApplyPress(true, IsReducedMotion());
        }
    }

    private void OnSurfacePointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _rightHoldTimer.Stop();
        if (_rightHoldPointer is { } held) Surface.ReleasePointerCapture(held);
        _rightHoldPointer = null;
        _motion.ApplyPress(false, IsReducedMotion());
    }

    private void OnSurfaceDragEnter(object sender, DragEventArgs args)
    {
        var canAccept = CanAcceptData(args.DataView);
        args.AcceptedOperation = canAccept ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.Handled = canAccept;
        if (!canAccept)
        {
            ResetVisualDrag();
            return;
        }

        if (!_visualDragActive)
        {
            _visualDragActive = true;
            ++_visualDragGeneration;
            _visualDragCallbacks.DragApproaching(_monitor.Id);
        }
        _visualDragCallbacks.DragReadyChanged(_monitor.Id, true);
        _logger.LogInformation(
            "WinUI visual-surface DragEnter received on monitor {MonitorId}: StorageItems=true, root HWND {WindowHandle}.",
            _monitor.Id,
            _windowHandle);
    }

    private void OnSurfaceDragOver(object sender, DragEventArgs args)
    {
        var canAccept = CanAcceptData(args.DataView);
        args.AcceptedOperation = canAccept ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.Handled = canAccept;
        if (canAccept)
        {
            if (!_visualDragActive)
            {
                _visualDragActive = true;
                ++_visualDragGeneration;
                _visualDragCallbacks.DragApproaching(_monitor.Id);
            }
            _visualDragCallbacks.DragReadyChanged(_monitor.Id, true);
        }
        else
        {
            ResetVisualDrag();
        }
    }

    private void OnSurfaceDragLeave(object sender, DragEventArgs args)
    {
        if (!_visualDragActive)
        {
            return;
        }

        ResetVisualDrag();
        _logger.LogInformation(
            "WinUI visual-surface DragLeave received on monitor {MonitorId}.",
            _monitor.Id);
    }

    private async void OnSurfaceDrop(object sender, DragEventArgs args)
    {
        if (!CanAcceptData(args.DataView))
        {
            args.AcceptedOperation = DataPackageOperation.None;
            ResetVisualDrag();
            return;
        }

        args.Handled = true;
        var generation = _visualDragGeneration;
        var outer = _visualDragCallbacks.CaptureGuard?.Invoke();
        bool Current() => !_closing && generation == _visualDragGeneration && (outer?.Invoke() ?? true);
        // OLE can begin another gesture while this payload provider is still asynchronous.
        _visualDragActive = false;
        void Finish()
        {
            if (!Current()) return;
            _visualDragActive = false;
            _visualDragCallbacks.DragLeft(_monitor.Id);
        }
        var deferral = args.GetDeferral();
        try
        {
            // StorageItems are the authoritative payload when a producer exposes both a
            // display text label and files. Consuming text first made mixed drags silently
            // add the label instead of the files.
            if (args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await args.DataView.GetStorageItemsAsync();
                var paths = items
                    .Where(static item => item is IStorageFile or IStorageFolder)
                    .Select(static item => item.Path)
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                args.AcceptedOperation = paths.Length > 0
                    ? DataPackageOperation.Copy
                    : DataPackageOperation.None;
                _logger.LogInformation(
                    "WinUI visual-surface Drop received on monitor {MonitorId}: offered item count {ItemCount}, accepted path count {PathCount}.",
                    _monitor.Id,
                    items.Count,
                    paths.Length);
                if (paths.Length == 0)
                {
                    Finish();
                    return;
                }

                if (_visualDragCallbacks.GuardedDropped is { } guarded)
                    await guarded(_monitor.Id, paths, Current);
                else await _visualDragCallbacks.Dropped(_monitor.Id, paths);
                // Use the same cleanup path as DragLeave. The drop callback normally clears
                // ownership in OverlayWindowService, but keeping this local state transition
                // explicit also covers direct/test callbacks and guarantees the visual target
                // cannot remain in a drag-ready state after a successful drop.
                Finish();
                return;
            }

            if (args.DataView.Contains(StandardDataFormats.WebLink) ||
                args.DataView.Contains(StandardDataFormats.Text))
            {
                var text = args.DataView.Contains(StandardDataFormats.WebLink)
                    ? (await args.DataView.GetWebLinkAsync()).AbsoluteUri
                    : await args.DataView.GetTextAsync();
                await _viewModel.CompleteVisibleTextDropAsync(_monitor.Id, text, isCurrent: Current);
                args.AcceptedOperation = DataPackageOperation.Copy;
                // Text drops do not go through the file-drop callback's common cleanup path.
                // Reset after completion so the completed drop is not immediately cancelled.
                Finish();
                return;
            }

            args.AcceptedOperation = DataPackageOperation.None;
            Finish();
        }
        catch (Exception exception)
        {
            args.AcceptedOperation = DataPackageOperation.None;
            _logger.LogWarning(exception, "Visible Overlay StorageItems drop failed.");
            Finish();
        }
        finally { deferral.Complete(); }
    }

    private void ResetVisualDrag()
    {
        if (!_visualDragActive)
        {
            return;
        }

        _visualDragActive = false;
        _visualDragCallbacks.DragLeft(_monitor.Id);
    }

    private static bool CanAcceptData(DataPackageView data) =>
        data.Contains(StandardDataFormats.StorageItems) ||
        data.Contains(StandardDataFormats.Text) ||
        data.Contains(StandardDataFormats.WebLink);

    private static ItemCardViewModel? GetCard(object sender) => sender switch
    {
        FrameworkElement { Tag: ItemCardViewModel card } => card,
        FrameworkElement { DataContext: ItemCardViewModel card } => card,
        _ => null,
    };
}

public sealed class OverlayPlacementEditEventArgs(OverlayCustomPlacement placement) : EventArgs
{
    public OverlayCustomPlacement Placement { get; } = placement;
}
