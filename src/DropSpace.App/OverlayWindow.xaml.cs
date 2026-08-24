using System.Diagnostics;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.DragDrop;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace DropSpace.App;

public sealed partial class OverlayWindow : Window
{
    private const double HostWidth = 600;
    private const double HostHeight = OverlayPlacementPolicy.MinimumHostHeightDips;
    private readonly OverlayViewModel _viewModel;
    private readonly IAppStringLocalizer _strings;
    private readonly MonitorDescriptor _monitor;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly Action _openMainWindow;
    private readonly ILogger<OverlayWindow> _logger;
    private readonly DragActivationCallbacks _visualDragCallbacks;
    private readonly OleDragDropService _dragDropService;
    private readonly nint _windowHandle;
    private OleDropTargetRegistration? _nativeDropTarget;
    private readonly OverlayMotionController _motion = new(OverlayMotionValues.Hidden);
    private OverlayState _previousState = OverlayState.Hidden;
    private long _lastFrameTimestamp;
    private bool _isActiveWindow;
    private bool _isVisible;
    private bool _hasFrameSubscription;
    private bool _hideWhenSettled;
    private bool _suppressedForFullscreen;
    private long _regionFailureCount;
    private bool _visualDragActive;
    private OverlayResolvedPlacement _resolvedPlacement;
    private OverlayVisualPhase _visualPhase = OverlayVisualPhase.Invisible;
    private readonly OverlayPlacementEditSession _placementEdit = new();
    private bool _placementEditActive;

    public OverlayWindow(
        OverlayViewModel viewModel,
        IAppStringLocalizer strings,
        MonitorDescriptor monitor,
        MonitorLayoutService monitorLayout,
        OleDragDropService dragDropService,
        DragActivationCallbacks dragCallbacks,
        Action openMainWindow,
        ILogger<OverlayWindow> logger)
    {
        _viewModel = viewModel;
        _strings = strings;
        _monitor = monitor;
        _monitorLayout = monitorLayout;
        _openMainWindow = openMainWindow;
        _logger = logger;
        _visualDragCallbacks = dragCallbacks;
        _dragDropService = dragDropService;
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

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        _windowHandle = WindowNative.GetWindowHandle(this);
        OverlayWindowInterop.ConfigureVisualWindow(_windowHandle);
        _resolvedPlacement = ResolvePlacement(
            FileDragWakeMode.SmartExperimental,
            new OverlayMonitorPlacement(OverlayPlacementMode.Automatic, 0, 0));
        PositionFixedHost();
        OverlayWindowInterop.ApplyEmptyRegion(_windowHandle);
        OverlayWindowInterop.Hide(_windowHandle);
    }

    public string MonitorId => _monitor.Id;

    public bool IsPlacementEditing => _placementEditActive;

    public event EventHandler<OverlayPlacementEditEventArgs>? PlacementCommitted;

    public event EventHandler? PlacementCancelled;

    internal bool HasActiveFrameSubscription => _hasFrameSubscription;

    internal long RegionFailureCount => Interlocked.Read(ref _regionFailureCount);

    internal void VerifyLocalizedResources()
    {
        VerifyResourceValue(Title, "OverlayWindow.Title");
        VerifyResourceValue(CompactSubtitleText.Text, "OverlayCompactSubtitle.Text");
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

            ApplyMotionFrame(controller.Current);
        }
    }

    public void ApplySnapshot(
        OverlaySnapshot snapshot,
        bool isActiveWindow,
        bool activationEnabled,
        FileDragWakeMode wakeMode,
        OverlayMonitorPlacement placement)
    {
        if (_placementEditActive)
        {
            _resolvedPlacement = ResolvePlacement(wakeMode, new OverlayMonitorPlacement(
                OverlayPlacementMode.Custom,
                _placementEdit.Preview.X,
                _placementEdit.Preview.Y));
            PositionFixedHost();
            return;
        }

        if (_visualPhase == OverlayVisualPhase.Exiting && snapshot.State != OverlayState.Hidden)
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

        var suppressedForFullscreen = snapshot.State is not (OverlayState.DragApproaching or OverlayState.DragReady) &&
                                      _monitorLayout.IsForegroundFullscreen(_monitor);
        if (suppressedForFullscreen)
        {
            BeginFullscreenSuppression(snapshot, wakeMode);
            return;
        }

        if (_suppressedForFullscreen)
        {
            _logger.LogInformation(
                "Full-screen suppression ended on monitor {MonitorId}; restoring the overlay with its current spring state.",
                _monitor.Id);
        }

        _suppressedForFullscreen = false;
        _hideWhenSettled = false;
        _resolvedPlacement = ResolvePlacement(wakeMode, placement);
        PositionFixedHost();

        if (snapshot.State == OverlayState.Hidden)
        {
            HideImmediately();
            _previousState = snapshot.State;
            return;
        }

        var topOffset = _resolvedPlacement.SurfaceTopOffsetDips;
        var target = CreateMotionTarget(snapshot.State, topOffset);

        EnsureVisualHostShown(snapshot.State == OverlayState.Expanded);
        PrepareContentForTarget(target);
        if (_previousState == OverlayState.DragReady && snapshot.State == OverlayState.Compact)
        {
            _motion.PulseDropTarget(0.94);
        }

        _motion.SetTarget(target, IsReducedMotion());
        StartAnimationFrames();
        _previousState = snapshot.State;
    }

    public void CloseForShutdown()
    {
        if (_placementEditActive)
        {
            EndPlacementEditVisuals();
        }
        StopAnimationFrames();
        RevokeNativeDropTarget();
        Close();
    }

    private void EnsureVisualHostShown(bool allowActivation)
    {
        EnsureNativeDropTargetRegistered();
        PositionFixedHost();
        OverlayWindowInterop.SetNoActivate(_windowHandle, !allowActivation);
        if (_isVisible)
        {
            OverlayWindowInterop.ShowNoActivateAndTopmost(_windowHandle);
            return;
        }

        ApplyMotionFrame(_motion.Current);
        OverlayWindowInterop.ShowNoActivateAndTopmost(_windowHandle);
        _isVisible = true;
    }

    private void HideImmediately()
    {
        StopAnimationFrames();
        Surface.Opacity = 0;
        ShadowSurface.Opacity = 0;
        CompactPanel.Visibility = Visibility.Collapsed;
        DragPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        OverlayWindowInterop.ApplyEmptyRegion(_windowHandle);
        OverlayWindowInterop.Hide(_windowHandle);
        RevokeNativeDropTarget();
        _motion.SnapTo(OverlayMotionValues.Hidden);
        _isVisible = false;
        _hideWhenSettled = false;
        _visualPhase = OverlayVisualPhase.Invisible;
    }

    private void BeginFullscreenSuppression(OverlaySnapshot snapshot, FileDragWakeMode wakeMode)
    {
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

    private void PositionFixedHost()
    {
        var width = ToPixels(HostWidth);
        var height = ToPixels(HostHeight);
        AppWindow.MoveAndResize(new RectInt32(
            _resolvedPlacement.HostLeftPixels,
            _resolvedPlacement.HostTopPixels,
            width,
            height));
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
                wakeMode),
            placement);

    private void StartAnimationFrames()
    {
        if (_hasFrameSubscription)
        {
            return;
        }

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnAnimationFrame;
        _hasFrameSubscription = true;
    }

    private void StopAnimationFrames()
    {
        if (!_hasFrameSubscription)
        {
            return;
        }

        CompositionTarget.Rendering -= OnAnimationFrame;
        _hasFrameSubscription = false;
    }

    private void OnAnimationFrame(object? sender, object args)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastFrameTimestamp, now);
        _lastFrameTimestamp = now;
        _motion.Step(elapsed);
        ApplyMotionFrame(_motion.Current);
        if (_motion.IsAnimating)
        {
            return;
        }

        StopAnimationFrames();
        if (_hideWhenSettled)
        {
            var current = _motion.Current.ProjectToSafeRange();
            var target = _motion.Target.ProjectToSafeRange();
            if (current.Opacity > 0.01 ||
                Math.Abs(current.Width - target.Width) > 4 ||
                Math.Abs(current.Height - target.Height) > 4)
            {
                StartAnimationFrames();
                return;
            }
            _hideWhenSettled = false;
            Surface.Opacity = 0;
            ShadowSurface.Opacity = 0;
            CollapseInvisibleContent(_motion.Current);
            OverlayWindowInterop.ApplyEmptyRegion(_windowHandle);
            OverlayWindowInterop.Hide(_windowHandle);
            RevokeNativeDropTarget();
            _isVisible = false;
            _visualPhase = OverlayVisualPhase.Invisible;
            return;
        }

        _visualPhase = OverlayVisualPhase.Visible;

        var state = _viewModel.Snapshot.State;
        if (!_isActiveWindow)
        {
            return;
        }

        if (state == OverlayState.Dismissing)
        {
            _viewModel.CompleteDismissal();
        }
    }

    private void ApplyMotionFrame(OverlayMotionValues values)
    {
        values = values.ProjectToSafeRange();
        Surface.Width = values.Width;
        Surface.Height = values.Height;
        Surface.CornerRadius = new CornerRadius(
            values.TopRadius,
            values.TopRadius,
            values.BottomRadius,
            values.BottomRadius);
        SurfaceTransform.TranslateY = values.TopOffset;
        SurfaceTransform.ScaleX = values.DropTargetScale;
        SurfaceTransform.ScaleY = values.DropTargetScale;
        Surface.Opacity = values.Opacity;
        ShadowSurface.Width = values.Width;
        ShadowSurface.Height = values.Height;
        ShadowSurface.CornerRadius = Surface.CornerRadius;
        ShadowTransform.TranslateY = values.TopOffset + 5;
        ShadowTransform.ScaleX = values.DropTargetScale;
        ShadowTransform.ScaleY = values.DropTargetScale;
        ShadowSurface.Opacity = values.Opacity * values.ShadowOpacity * 0.35;
        CompactPanel.Opacity = values.CompactContent;
        DragPanel.Opacity = values.DragContent;
        ExpandedPanel.Opacity = values.ExpandedContent;
        CollapseInvisibleContent(values);

        var width = ToPixels(values.Width * values.DropTargetScale);
        var height = ToPixels(values.Height * values.DropTargetScale);
        var left = (ToPixels(HostWidth) - width) / 2;
        var top = ToPixels(values.TopOffset + values.Height * (1 - values.DropTargetScale) / 2);
        if (!OverlayWindowInterop.ApplyVisualRegion(
            _windowHandle,
            left,
            top,
            width,
            height,
            ToPixels(values.TopRadius),
            ToPixels(values.BottomRadius)))
        {
            Interlocked.Increment(ref _regionFailureCount);
            OverlayWindowInterop.ApplyEmptyRegion(_windowHandle);
            _logger.LogError(
                "The overlay HRGN could not be applied for monitor {MonitorId}; the unsafe frame was hidden.",
                _monitor.Id);
        }
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

    private bool IsReducedMotion() => _viewModel.MotionPreference switch
    {
        OverlayMotionPreference.Reduced => true,
        OverlayMotionPreference.Full => false,
        _ => !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
    };

    private int ToPixels(double dips) => Math.Max(0, (int)Math.Round(dips * _monitor.Scale));

    private static OverlayMotionValues CreateMotionTarget(OverlayState state, double topOffset)
    {
        return state switch
        {
            OverlayState.DragApproaching => Create(300, 54, topOffset, 27, 0, 1, 0),
            OverlayState.DragReady => Create(430, 92, topOffset, 30, 0, 1, 0),
            OverlayState.Compact => Create(340, 64, topOffset, 32, 1, 0, 0),
            OverlayState.Expanded => Create(560, 340, topOffset, 28, 0, 0, 1),
            OverlayState.Dismissing or OverlayState.Hidden => new OverlayMotionValues(
                120,
                12,
                topOffset,
                6,
                6,
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

    public void BeginPlacementEdit(OverlayCustomPlacement initial)
    {
        _placementEdit.Arm(initial);
        _placementEditActive = true;
        _isActiveWindow = true;
        _suppressedForFullscreen = false;
        _hideWhenSettled = false;
        _resolvedPlacement = ResolvePlacement(
            _viewModel.FileDragWakeMode,
            new OverlayMonitorPlacement(OverlayPlacementMode.Custom, initial.X, initial.Y));
        PositionFixedHost();
        RevokeNativeDropTarget();
        PlacementEditSurface.Visibility = Visibility.Visible;
        PlacementEditSurface.IsHitTestVisible = true;
        PrepareContentForTarget(CreateMotionTarget(OverlayState.Compact, 0));
        _motion.SetTarget(CreateMotionTarget(OverlayState.Compact, 0), IsReducedMotion());
        OverlayWindowInterop.SetNoActivate(_windowHandle, true);
        if (!_isVisible)
        {
            ApplyMotionFrame(_motion.Current);
            OverlayWindowInterop.ShowNoActivateAndTopmost(_windowHandle);
            _isVisible = true;
        }
        else
        {
            OverlayWindowInterop.ShowNoActivateAndTopmost(_windowHandle);
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
        PositionFixedHost();
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

        var committed = _placementEdit.Commit();
        PlacementEditSurface.ReleasePointerCapture(args.Pointer);
        EndPlacementEditVisuals();
        PlacementCommitted?.Invoke(this, new OverlayPlacementEditEventArgs(committed));
        args.Handled = true;
    }

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
        try
        {
            await _viewModel.ExpandAsync();
            OverlayWindowInterop.SetNoActivate(_windowHandle, false);
            Activate();
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Overlay expansion failed.");
        }
    }

    private void OnCollapseClicked(object sender, RoutedEventArgs args) => _viewModel.Collapse();

    private void OnOpenMainWindowClicked(object sender, RoutedEventArgs args)
    {
        _openMainWindow();
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
        if (_viewModel.Snapshot.State == OverlayState.Compact)
        {
            Surface.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(248, 20, 20, 27));
        }
    }

    private void OnSurfacePointerExited(object sender, PointerRoutedEventArgs args)
    {
        Surface.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(242, 13, 13, 17));
    }

    private void OnSurfaceDragEnter(object sender, DragEventArgs args)
    {
        var canAccept = CanAcceptData(args.DataView);
        args.AcceptedOperation = canAccept ? DataPackageOperation.Copy : DataPackageOperation.None;
        args.Handled = canAccept;
        if (!canAccept)
        {
            return;
        }

        _visualDragActive = true;
        _visualDragCallbacks.DragApproaching(_monitor.Id);
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
            _visualDragCallbacks.DragReadyChanged(_monitor.Id, true);
        }
    }

    private void OnSurfaceDragLeave(object sender, DragEventArgs args)
    {
        if (!_visualDragActive)
        {
            return;
        }

        _visualDragActive = false;
        _visualDragCallbacks.DragLeft(_monitor.Id);
        _logger.LogInformation(
            "WinUI visual-surface DragLeave received on monitor {MonitorId}.",
            _monitor.Id);
    }

    private async void OnSurfaceDrop(object sender, DragEventArgs args)
    {
        if (!CanAcceptData(args.DataView))
        {
            args.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        args.Handled = true;
        _visualDragActive = false;
        try
        {
            if (args.DataView.Contains(StandardDataFormats.WebLink) ||
                args.DataView.Contains(StandardDataFormats.Text))
            {
                var text = args.DataView.Contains(StandardDataFormats.WebLink)
                    ? (await args.DataView.GetWebLinkAsync()).AbsoluteUri
                    : await args.DataView.GetTextAsync();
                await _viewModel.CompleteVisibleTextDropAsync(_monitor.Id, text);
                args.AcceptedOperation = DataPackageOperation.Copy;
                return;
            }

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
                _visualDragCallbacks.DragLeft(_monitor.Id);
                return;
            }

            await _visualDragCallbacks.Dropped(_monitor.Id, paths);
        }
        catch (Exception exception)
        {
            args.AcceptedOperation = DataPackageOperation.None;
            _logger.LogWarning(exception, "Visible Overlay StorageItems drop failed.");
            _visualDragCallbacks.DragLeft(_monitor.Id);
        }
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
