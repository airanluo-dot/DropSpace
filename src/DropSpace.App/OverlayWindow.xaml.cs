using System.Diagnostics;
using System.Numerics;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace DropSpace.App;

public sealed partial class OverlayWindow : Window
{
    private const double ActivationWidth = 280;
    private const double ActivationHeight = 3;
    private readonly OverlayViewModel _viewModel;
    private readonly MonitorDescriptor _monitor;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly Action _openMainWindow;
    private readonly ILogger<OverlayWindow> _logger;
    private readonly nint _windowHandle;
    private OverlayGeometry _currentGeometry = OverlayGeometry.Hidden;
    private OverlayDisplayMode _currentDisplayMode = OverlayDisplayMode.DynamicIsland;
    private bool _isActiveWindow;
    private bool _isModeMorphRunning;
    private long _animationRevision;
    private long _modeMorphStarted;
    private double _modeFromOffset;
    private double _modeToOffset;
    private double _modeFromTopRadius;
    private double _modeToTopRadius;

    public OverlayWindow(
        OverlayViewModel viewModel,
        MonitorDescriptor monitor,
        MonitorLayoutService monitorLayout,
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
        OverlayWindowInterop.ConfigureToolWindow(_windowHandle);
        ConfigureActivationZone();
    }

    public string MonitorId => _monitor.Id;

    internal bool HasActiveFrameSubscription => _isModeMorphRunning;

    public void ApplySnapshot(OverlaySnapshot snapshot, bool isActiveWindow, bool activationEnabled)
    {
        _isActiveWindow = isActiveWindow;
        if (!activationEnabled)
        {
            AppWindow.Hide();
            return;
        }

        var suppressedForFullscreen = snapshot.State is not (OverlayState.DragApproaching or OverlayState.DragReady) &&
                                      _monitorLayout.IsForegroundFullscreen(_monitor);
        if (!isActiveWindow || snapshot.State == OverlayState.Hidden || suppressedForFullscreen)
        {
            ConfigureActivationZone();
            if (isActiveWindow && snapshot.State == OverlayState.ModeTransition && suppressedForFullscreen)
            {
                DispatcherQueue.TryEnqueue(_viewModel.CompleteModeTransition);
            }

            return;
        }

        AppWindow.Show(false);
        OverlayWindowInterop.SetNoActivate(_windowHandle, snapshot.State != OverlayState.Expanded);
        if (snapshot.State != OverlayState.ModeTransition)
        {
            UpdatePanelVisibility(snapshot.State);
        }

        if (snapshot.State == OverlayState.ModeTransition)
        {
            StartModeMorph(snapshot.TargetDisplayMode);
            return;
        }

        StopModeMorph();
        _currentDisplayMode = snapshot.DisplayMode;
        var geometry = OverlayGeometry.For(snapshot.State, snapshot.DisplayMode);
        _ = AnimateToAsync(geometry, snapshot.State);
    }

    public void CloseForShutdown()
    {
        StopModeMorph();
        Close();
    }

    private void ConfigureActivationZone()
    {
        _animationRevision++;
        StopModeMorph();
        _isActiveWindow = false;
        Surface.Opacity = 0;
        Root.Opacity = 1;
        CompactPanel.Visibility = Visibility.Collapsed;
        DragPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        var width = ToPixels(ActivationWidth);
        var height = Math.Max(1, ToPixels(ActivationHeight));
        var x = _monitor.Left + (_monitor.Width - width) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, _monitor.Top, width, height));
        OverlayWindowInterop.ApplyActivationRegion(_windowHandle, width, height);
        OverlayWindowInterop.SetNoActivate(_windowHandle, true);
        AppWindow.Show(false);
        _currentGeometry = OverlayGeometry.Hidden;
    }

    private async Task AnimateToAsync(OverlayGeometry target, OverlayState state)
    {
        var revision = ++_animationRevision;
        var previous = _currentGeometry;
        _currentGeometry = target;
        Surface.Opacity = 1;
        var reducedMotion = IsReducedMotion();
        var targetWidth = ToPixels(target.Width);
        var targetHeight = ToPixels(target.Height + target.TopOffset);
        var previousWidth = ToPixels(previous.Width);
        var previousHeight = ToPixels(previous.Height + previous.TopOffset);
        var hostWidth = Math.Max(targetWidth, previousWidth);
        var hostHeight = Math.Max(targetHeight, previousHeight);
        MoveHost(hostWidth, hostHeight);

        Surface.Width = target.Width;
        Surface.Height = target.Height;
        SurfaceTranslation.Y = target.TopOffset;
        Surface.CornerRadius = target.CornerRadius;

        var visual = ElementCompositionPreview.GetElementVisual(Surface);
        visual.CenterPoint = new Vector3((float)(target.Width / 2), (float)(target.Height / 2), 0);
        visual.Scale = new Vector3(
            (float)Math.Clamp(previous.Width / Math.Max(1, target.Width), 0.35, 2.5),
            (float)Math.Clamp(previous.Height / Math.Max(1, target.Height), 0.2, 4),
            1);
        visual.Opacity = (float)previous.Opacity;

        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        batch.Completed += (_, _) => completion.TrySetResult();

        if (reducedMotion)
        {
            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(1, Vector3.One);
            scale.Duration = TimeSpan.FromMilliseconds(90);
            visual.StartAnimation(nameof(visual.Scale), scale);
        }
        else
        {
            var scale = compositor.CreateSpringVector3Animation();
            scale.FinalValue = Vector3.One;
            scale.DampingRatio = 0.82f;
            scale.Period = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation(nameof(visual.Scale), scale);
        }

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, (float)target.Opacity);
        opacity.Duration = TimeSpan.FromMilliseconds(reducedMotion ? 80 : 150);
        visual.StartAnimation(nameof(visual.Opacity), opacity);
        batch.End();
        await completion.Task;

        if (revision != _animationRevision)
        {
            return;
        }

        visual.Scale = Vector3.One;
        visual.Opacity = (float)target.Opacity;
        MoveHost(targetWidth, targetHeight);
        OverlayWindowInterop.ApplyRegion(
            _windowHandle,
            targetWidth,
            targetHeight,
            ToPixels(target.TopOffset),
            ToPixels(target.BottomRadius),
            _currentDisplayMode);

        if (!_isActiveWindow)
        {
            return;
        }

        if (state == OverlayState.Dismissing)
        {
            _viewModel.CompleteDismissal();
        }
    }

    private void StartModeMorph(OverlayDisplayMode targetMode)
    {
        if (_isModeMorphRunning && targetMode == _currentDisplayMode)
        {
            return;
        }

        StopModeMorph();
        _modeFromOffset = SurfaceTranslation.Y;
        _modeToOffset = targetMode == OverlayDisplayMode.DynamicIsland ? 8 : 0;
        _modeFromTopRadius = Surface.CornerRadius.TopLeft;
        _modeToTopRadius = targetMode == OverlayDisplayMode.DynamicIsland
            ? _currentGeometry.BottomRadius
            : 0;
        _currentDisplayMode = targetMode;
        _modeMorphStarted = Stopwatch.GetTimestamp();
        _isModeMorphRunning = true;
        CompositionTarget.Rendering += OnModeMorphFrame;
    }

    private void OnModeMorphFrame(object? sender, object args)
    {
        var duration = IsReducedMotion() ? 100d : 260d;
        var elapsed = Stopwatch.GetElapsedTime(_modeMorphStarted).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / duration, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var topOffset = Lerp(_modeFromOffset, _modeToOffset, eased);
        var topRadius = Lerp(_modeFromTopRadius, _modeToTopRadius, eased);
        SurfaceTranslation.Y = topOffset;
        Surface.CornerRadius = new CornerRadius(
            topRadius,
            topRadius,
            _currentGeometry.BottomRadius,
            _currentGeometry.BottomRadius);

        var width = ToPixels(_currentGeometry.Width);
        var height = ToPixels(_currentGeometry.Height + Math.Max(_modeFromOffset, _modeToOffset));
        MoveHost(width, height);
        OverlayWindowInterop.ApplyRegion(
            _windowHandle,
            width,
            height,
            ToPixels(topOffset),
            ToPixels(_currentGeometry.BottomRadius),
            _currentDisplayMode);

        if (progress < 1)
        {
            return;
        }

        StopModeMorph();
        if (_isActiveWindow)
        {
            _viewModel.CompleteModeTransition();
        }
    }

    private void StopModeMorph()
    {
        if (!_isModeMorphRunning)
        {
            return;
        }

        CompositionTarget.Rendering -= OnModeMorphFrame;
        _isModeMorphRunning = false;
    }

    private void MoveHost(int width, int height)
    {
        var x = _monitor.Left + (_monitor.Width - width) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, _monitor.Top, width, height));
    }

    private void UpdatePanelVisibility(OverlayState state)
    {
        CompactPanel.Visibility = state == OverlayState.Compact ? Visibility.Visible : Visibility.Collapsed;
        DragPanel.Visibility = state is OverlayState.DragApproaching or OverlayState.DragReady
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExpandedPanel.Visibility = state == OverlayState.Expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsReducedMotion()
    {
        return _viewModel.MotionPreference switch
        {
            OverlayMotionPreference.Reduced => true,
            OverlayMotionPreference.Full => false,
            _ => !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
        };
    }

    private int ToPixels(double dips) => Math.Max(0, (int)Math.Round(dips * _monitor.Scale));

    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;

    private void OnDragEnter(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.StorageItems))
        {
            args.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        args.AcceptedOperation = DataPackageOperation.Copy;
        args.DragUIOverride.Caption = "添加到 DropSpace";
        args.DragUIOverride.IsCaptionVisible = true;
        _viewModel.BeginDragApproach(MonitorId);
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        var ready = args.DataView.Contains(StandardDataFormats.StorageItems);
        args.AcceptedOperation = ready ? DataPackageOperation.Copy : DataPackageOperation.None;
        _viewModel.SetDragReady(ready);
    }

    private void OnDragLeave(object sender, DragEventArgs args) => _viewModel.CancelDrag();

    private async void OnDrop(object sender, DragEventArgs args)
    {
        try
        {
            if (!args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                _viewModel.CancelDrag();
                return;
            }

            var items = await args.DataView.GetStorageItemsAsync();
            var paths = items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            await _viewModel.CompleteDropAsync(MonitorId, paths);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Overlay drop failed.");
            _viewModel.CancelDrag();
        }
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
        if (_viewModel.Snapshot.State != OverlayState.Compact)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(Surface);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, 0.96f);
        animation.Duration = TimeSpan.FromMilliseconds(100);
        visual.StartAnimation(nameof(visual.Opacity), animation);
    }

    private void OnSurfacePointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (_viewModel.Snapshot.State != OverlayState.Compact)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(Surface);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, 1f);
        animation.Duration = TimeSpan.FromMilliseconds(100);
        visual.StartAnimation(nameof(visual.Opacity), animation);
    }

    private static ItemCardViewModel? GetCard(object sender) => sender switch
    {
        FrameworkElement { Tag: ItemCardViewModel card } => card,
        FrameworkElement { DataContext: ItemCardViewModel card } => card,
        _ => null,
    };

    private sealed record OverlayGeometry(
        double Width,
        double Height,
        double TopOffset,
        double TopRadius,
        double BottomRadius,
        double Opacity)
    {
        public static OverlayGeometry Hidden { get; } = new(ActivationWidth, ActivationHeight, 0, 0, 0, 0);

        public CornerRadius CornerRadius => new(TopRadius, TopRadius, BottomRadius, BottomRadius);

        public static OverlayGeometry For(OverlayState state, OverlayDisplayMode mode)
        {
            var topOffset = mode == OverlayDisplayMode.DynamicIsland ? 8 : 0;
            return state switch
            {
                OverlayState.DragApproaching => Create(300, 54, topOffset, 27, mode, 0.94),
                OverlayState.DragReady => Create(430, 92, topOffset, 30, mode, 1),
                OverlayState.Compact => Create(340, 64, topOffset, 32, mode, 1),
                OverlayState.Expanded => Create(560, 340, topOffset, 28, mode, 1),
                OverlayState.Dismissing => Create(140, 20, topOffset, 10, mode, 0),
                _ => Hidden,
            };
        }

        private static OverlayGeometry Create(
            double width,
            double height,
            double topOffset,
            double radius,
            OverlayDisplayMode mode,
            double opacity) => new(
                width,
                height,
                topOffset,
                mode == OverlayDisplayMode.DynamicIsland ? radius : 0,
                radius,
                opacity);
    }
}
