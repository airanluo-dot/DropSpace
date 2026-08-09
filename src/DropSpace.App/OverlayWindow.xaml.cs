using System.Diagnostics;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace DropSpace.App;

public sealed partial class OverlayWindow : Window
{
    private const double HostWidth = 600;
    private const double HostHeight = 360;
    private readonly OverlayViewModel _viewModel;
    private readonly MonitorDescriptor _monitor;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly Action _openMainWindow;
    private readonly ILogger<OverlayWindow> _logger;
    private readonly nint _windowHandle;
    private readonly IDisposable _nativeDropTarget;
    private readonly OverlayMotionController _motion = new(OverlayMotionValues.Hidden);
    private OverlayState _lastStableState = OverlayState.Hidden;
    private OverlayState _previousState = OverlayState.Hidden;
    private OverlayDisplayMode _renderDisplayMode = OverlayDisplayMode.DynamicIsland;
    private long _lastFrameTimestamp;
    private bool _isActiveWindow;
    private bool _isVisible;
    private bool _hasFrameSubscription;
    private bool _hideWhenSettled;
    private bool _suppressedForFullscreen;
    private long _regionFailureCount;

    public OverlayWindow(
        OverlayViewModel viewModel,
        MonitorDescriptor monitor,
        MonitorLayoutService monitorLayout,
        OleDragDropService dragDropService,
        DragActivationCallbacks dragCallbacks,
        Action openMainWindow,
        ILogger<OverlayWindow> logger)
    {
        _viewModel = viewModel;
        _monitor = monitor;
        _monitorLayout = monitorLayout;
        _openMainWindow = openMainWindow;
        _logger = logger;
        InitializeComponent();
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
        PositionFixedHost();
        OverlayWindowInterop.ApplyEmptyRegion(_windowHandle);
        OverlayWindowInterop.Hide(_windowHandle);
        _nativeDropTarget = dragDropService.RegisterVisualTarget(
            _windowHandle,
            monitor.Id,
            dragCallbacks);
    }

    public string MonitorId => _monitor.Id;

    internal bool HasActiveFrameSubscription => _hasFrameSubscription;

    internal long RegionFailureCount => Interlocked.Read(ref _regionFailureCount);

    internal long RunNotchGeometryStress(int cycles)
    {
        if (cycles is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(cycles));
        }

        var failuresBefore = RegionFailureCount;
        var island = CreateMotionTarget(OverlayState.Compact, OverlayDisplayMode.DynamicIsland);
        var notch = CreateMotionTarget(OverlayState.Compact, OverlayDisplayMode.Notch);
        var controller = new OverlayMotionController(island);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            controller.SetTarget(notch, reducedMotion: false);
            ApplyStressFrames(controller, OverlayDisplayMode.Notch, 2 + cycle % 5);
            controller.SetTarget(island, reducedMotion: false);
            ApplyStressFrames(controller, OverlayDisplayMode.DynamicIsland, 1 + cycle % 4);
            controller.SetTarget(notch, reducedMotion: false);
            ApplyStressFrames(controller, OverlayDisplayMode.Notch, 1 + cycle % 3);
            controller.SetTarget(island, reducedMotion: false);
            ApplyStressFrames(controller, OverlayDisplayMode.DynamicIsland, 2 + cycle % 6);
        }

        for (var frame = 0; frame < 600 && controller.IsAnimating; frame++)
        {
            ApplyStressFrames(controller, OverlayDisplayMode.DynamicIsland, 1);
        }

        if (controller.IsAnimating || !controller.Current.IsApiSafe())
        {
            throw new InvalidOperationException("The real overlay geometry stress did not settle to an API-safe frame.");
        }

        return RegionFailureCount - failuresBefore;
    }

    private void ApplyStressFrames(
        OverlayMotionController controller,
        OverlayDisplayMode displayMode,
        int frames)
    {
        _renderDisplayMode = displayMode;
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

    public void ApplySnapshot(OverlaySnapshot snapshot, bool isActiveWindow, bool activationEnabled)
    {
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
            BeginFullscreenSuppression(snapshot);
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

        if (snapshot.State == OverlayState.Hidden ||
            snapshot.State == OverlayState.ModeTransition && snapshot.TemporaryItemCount == 0)
        {
            HideImmediately();
            _lastStableState = OverlayState.Hidden;
            _previousState = snapshot.State;
            if (snapshot.State == OverlayState.ModeTransition)
            {
                DispatcherQueue.TryEnqueue(_viewModel.CompleteModeTransition);
            }

            return;
        }

        if (snapshot.State is OverlayState.DragApproaching or OverlayState.DragReady or
            OverlayState.Compact or OverlayState.Expanded)
        {
            _lastStableState = snapshot.State;
        }

        var displayMode = snapshot.State == OverlayState.ModeTransition
            ? snapshot.TargetDisplayMode
            : snapshot.DisplayMode;
        _renderDisplayMode = displayMode;
        var targetState = snapshot.State == OverlayState.ModeTransition
            ? ResolveModeTransitionState(snapshot)
            : snapshot.State;
        var target = CreateMotionTarget(targetState, displayMode);

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
        StopAnimationFrames();
        _nativeDropTarget.Dispose();
        Close();
    }

    private OverlayState ResolveModeTransitionState(OverlaySnapshot snapshot)
    {
        if (_lastStableState is OverlayState.DragApproaching or OverlayState.DragReady or
            OverlayState.Compact or OverlayState.Expanded)
        {
            return _lastStableState;
        }

        return snapshot.TemporaryItemCount == 0 ? OverlayState.Hidden : OverlayState.Compact;
    }

    private void EnsureVisualHostShown(bool allowActivation)
    {
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
        CompactPanel.Visibility = Visibility.Collapsed;
        DragPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        OverlayWindowInterop.ApplyEmptyRegion(_windowHandle);
        OverlayWindowInterop.Hide(_windowHandle);
        _motion.SnapTo(OverlayMotionValues.Hidden);
        _isVisible = false;
        _hideWhenSettled = false;
    }

    private void BeginFullscreenSuppression(OverlaySnapshot snapshot)
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
            if (snapshot.State == OverlayState.ModeTransition)
            {
                DispatcherQueue.TryEnqueue(_viewModel.CompleteModeTransition);
            }

            return;
        }

        _suppressedForFullscreen = true;
        _hideWhenSettled = true;
        var displayMode = snapshot.State == OverlayState.ModeTransition
            ? snapshot.TargetDisplayMode
            : snapshot.DisplayMode;
        _renderDisplayMode = displayMode;
        var hiddenTarget = CreateMotionTarget(OverlayState.Hidden, displayMode);
        PrepareContentForTarget(hiddenTarget);
        _motion.SetTarget(hiddenTarget, IsReducedMotion());
        StartAnimationFrames();
    }

    private void PositionFixedHost()
    {
        var width = ToPixels(HostWidth);
        var height = ToPixels(HostHeight);
        var x = _monitor.Left + (_monitor.Width - width) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, _monitor.Top, width, height));
    }

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
            _hideWhenSettled = false;
            Surface.Opacity = 0;
            CollapseInvisibleContent(_motion.Current);
            OverlayWindowInterop.ApplyEmptyRegion(_windowHandle);
            OverlayWindowInterop.Hide(_windowHandle);
            _isVisible = false;
            if (_viewModel.Snapshot.State == OverlayState.ModeTransition)
            {
                _viewModel.CompleteModeTransition();
            }

            return;
        }

        var state = _viewModel.Snapshot.State;
        if (!_isActiveWindow)
        {
            return;
        }

        if (state == OverlayState.Dismissing)
        {
            _viewModel.CompleteDismissal();
        }
        else if (state == OverlayState.ModeTransition)
        {
            _viewModel.CompleteModeTransition();
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
            ToPixels(values.BottomRadius),
            _renderDisplayMode))
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

    private static OverlayMotionValues CreateMotionTarget(OverlayState state, OverlayDisplayMode mode)
    {
        var topOffset = mode == OverlayDisplayMode.DynamicIsland ? 8 : 0;
        return state switch
        {
            OverlayState.DragApproaching => Create(300, 54, topOffset, 27, mode, 0, 1, 0),
            OverlayState.DragReady => Create(430, 92, topOffset, 30, mode, 0, 1, 0),
            OverlayState.Compact => Create(340, 64, topOffset, 32, mode, 1, 0, 0),
            OverlayState.Expanded => Create(560, 340, topOffset, 28, mode, 0, 0, 1),
            OverlayState.Dismissing or OverlayState.Hidden => new OverlayMotionValues(
                120,
                12,
                topOffset,
                mode == OverlayDisplayMode.DynamicIsland ? 6 : 0,
                6,
                0,
                0,
                0,
                0,
                0.92),
            _ => OverlayMotionValues.Hidden,
        };
    }

    private static OverlayMotionValues Create(
        double width,
        double height,
        double topOffset,
        double radius,
        OverlayDisplayMode mode,
        double compactContent,
        double dragContent,
        double expandedContent) =>
        new(
            width,
            height,
            topOffset,
            mode == OverlayDisplayMode.DynamicIsland ? radius : 0,
            radius,
            1,
            compactContent,
            dragContent,
            expandedContent,
            1);

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
        var storageItems = args.Items
            .OfType<ItemCardViewModel>()
            .Select(card => card.DragStorageItem)
            .Where(item => item is not null)
            .Cast<Windows.Storage.IStorageItem>()
            .ToArray();
        if (storageItems.Length == 0)
        {
            args.Cancel = true;
            return;
        }

        args.Data.SetStorageItems(storageItems, readOnly: true);
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

    private static ItemCardViewModel? GetCard(object sender) => sender switch
    {
        FrameworkElement { Tag: ItemCardViewModel card } => card,
        FrameworkElement { DataContext: ItemCardViewModel card } => card,
        _ => null,
    };
}
