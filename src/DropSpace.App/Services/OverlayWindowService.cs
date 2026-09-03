using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Compatibility;
using DropSpace.Core.DragDrop;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services;

public sealed class OverlayWindowService : IDisposable
{
    private readonly OverlayViewModel _viewModel;
    private readonly IAppStringLocalizer _strings;
    private readonly MainViewModel _mainViewModel;
    private readonly QuickActionDialogService _quickActionDialog;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly IWindowsCapabilityService _capabilities;
    private readonly ForegroundWindowMonitor _foregroundWindowMonitor;
    private readonly OverlayStateMachine _stateMachine;
    private readonly OleDragDropService _dragDropService;
    private readonly DragSessionDetector _dragSessionDetector;
    private readonly GlobalQuickPanelHotkeyService _quickPanelHotkey;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OverlayWindowService> _logger;
    private readonly CrashDiagnosticsService _crashDiagnostics;
    private readonly List<OverlayWindow> _windows = [];
    private readonly List<DragActivationHost> _activationHosts = [];
    private DisplayTopologyWatcher? _displayTopologyWatcher;
    private MonitorDescriptor? _primaryMonitor;
    private Action? _openMainWindow;
    private DragTargetOwner _activeDragOwner;
    private long _activeSmartSessionId;
    private DragScreenPoint _activeSmartSessionPoint;
    private FileDragWakeMode? _configuredWakeMode;
    private OverlayWindow? _placementEditingWindow;
    private FileDragWakeMode? _placementInputRestoreMode;
    private bool _topologyRefreshPending;
    private bool _disposed;

    public OverlayWindowService(
        OverlayViewModel viewModel,
        IAppStringLocalizer strings,
        MainViewModel mainViewModel,
        QuickActionDialogService quickActionDialog,
        MonitorLayoutService monitorLayout,
        IWindowsCapabilityService capabilities,
        ForegroundWindowMonitor foregroundWindowMonitor,
        OverlayStateMachine stateMachine,
        OleDragDropService dragDropService,
        DragSessionDetector dragSessionDetector,
        GlobalQuickPanelHotkeyService quickPanelHotkey,
        DispatcherQueue dispatcher,
        ILoggerFactory loggerFactory,
        CrashDiagnosticsService crashDiagnostics)
    {
        _viewModel = viewModel;
        _strings = strings;
        _mainViewModel = mainViewModel;
        _quickActionDialog = quickActionDialog;
        _monitorLayout = monitorLayout;
        _capabilities = capabilities;
        _foregroundWindowMonitor = foregroundWindowMonitor;
        _stateMachine = stateMachine;
        _dragDropService = dragDropService;
        _dragSessionDetector = dragSessionDetector;
        _quickPanelHotkey = quickPanelHotkey;
        _dispatcher = dispatcher;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OverlayWindowService>();
        _crashDiagnostics = crashDiagnostics;
    }

    public async Task InitializeAsync(Action openMainWindow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openMainWindow);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windows.Count > 0)
        {
            return;
        }

        _openMainWindow = openMainWindow;
        CreateMonitorSurfaces();
        var primaryMonitor = _primaryMonitor
            ?? throw new InvalidOperationException("No primary monitor was available after creating overlay surfaces.");

        _viewModel.SnapshotChanged += OnSnapshotChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _mainViewModel.OverlayPlacementEditRequested += OnOverlayPlacementEditRequested;
        _foregroundWindowMonitor.ForegroundChanged += OnForegroundChanged;
        _foregroundWindowMonitor.Start();
        await _viewModel.InitializeAsync(primaryMonitor.Id, cancellationToken);
        _displayTopologyWatcher = new DisplayTopologyWatcher();
        _displayTopologyWatcher.Changed += OnDisplayTopologyChanged;
        _dragSessionDetector.CandidateStarted += OnSmartDragCandidateStarted;
        _dragSessionDetector.VerifiedFileDragStarted += OnSmartVerifiedFileDragStarted;
        _dragSessionDetector.CandidateEnded += OnSmartDragCandidateEnded;
        _dragSessionDetector.PlacementEditEscapeRequested += OnPlacementEditEscapeRequested;
        _quickPanelHotkey.Invoked += OnQuickPanelHotkeyInvoked;
        await _quickPanelHotkey.StartAsync(_viewModel.QuickPanelHotkey, cancellationToken);
        _dragSessionDetector.SetExcludedProcesses(_viewModel.SmartDragExcludedProcesses);
        ConfigureWakeMode(_viewModel.FileDragWakeMode);
        ApplySnapshot(_viewModel.Snapshot);
    }

    public async Task<OverlayLifecycleMetrics> RunLifecycleSmokeAsync(
        int cycles,
        CancellationToken cancellationToken = default)
    {
        if (cycles is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(cycles));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        var original = _viewModel.Snapshot;
        for (var index = 0; index < 5; index++)
        {
            ExerciseLifecycle();
        }

        var geometryStressCycles = 1_000;
        var regionFailures = _windows[0].RunGeometryStress(geometryStressCycles);
        if (regionFailures != 0)
        {
            throw new InvalidOperationException(
                $"Overlay geometry stress encountered {regionFailures} HRGN application failures.");
        }

        _stateMachine.Restore(1);
        await Task.Delay(750, cancellationToken);
        var compactVisualProbe = ProbeActiveVisualCenter();
        _stateMachine.Expand();
        await Task.Delay(750, cancellationToken);
        var expandedVisualProbe = ProbeActiveVisualCenter();
        var compactVisualTargetDiscoverable = compactVisualProbe.IsRootOrDescendant;
        var expandedVisualTargetDiscoverable = expandedVisualProbe.IsRootOrDescendant;
        // Activation-host discovery is meaningful only while the visual Overlay is truly hidden.
        _stateMachine.Restore(0);
        await Task.Delay(300, cancellationToken);
        if (!compactVisualTargetDiscoverable || !expandedVisualTargetDiscoverable)
        {
            throw new InvalidOperationException(
                $"WindowFromPoint did not resolve to the visible Overlay HWND or a WinUI descendant in Compact and Expanded states. " +
                $"Compact: root={compactVisualProbe.RootWindow}, visible={compactVisualProbe.IsRootVisible}, rect={compactVisualProbe.RootLeft},{compactVisualProbe.RootTop},{compactVisualProbe.RootWidth}x{compactVisualProbe.RootHeight}, " +
                $"discovered={compactVisualProbe.DiscoveredWindow}, class={compactVisualProbe.WindowClassName}, owned={compactVisualProbe.IsRootOrDescendant}; " +
                $"Expanded: root={expandedVisualProbe.RootWindow}, visible={expandedVisualProbe.IsRootVisible}, rect={expandedVisualProbe.RootLeft},{expandedVisualProbe.RootTop},{expandedVisualProbe.RootWidth}x{expandedVisualProbe.RootHeight}, " +
                $"discovered={expandedVisualProbe.DiscoveredWindow}, class={expandedVisualProbe.WindowClassName}, owned={expandedVisualProbe.IsRootOrDescendant}. " +
                $"Overlay state: {GetActiveWindow().NativeVisibilityDiagnostics}.");
        }

        var idleTopEdgePassThrough = _windows.All(window => window.ProbeIdleTopEdgePassThrough());
        if (!idleTopEdgePassThrough)
        {
            throw new InvalidOperationException(
                "A hidden visual Overlay still owned a top-edge WindowFromPoint hit.");
        }

        var wakeModeSwitchVerified = VerifyWakeModeSwitchOwnership(_viewModel.FileDragWakeMode);
        if (!wakeModeSwitchVerified)
        {
            throw new InvalidOperationException(
                "Smart, Classic and Disabled drag-wake modes did not transfer native target ownership cleanly.");
        }

        var smartObserverRegistered = VerifySmartObserverRegistration(_viewModel.FileDragWakeMode);
        if (!smartObserverRegistered)
        {
            throw new InvalidOperationException(
                "The Smart detector did not register its observation-only mouse and accessibility drag signal sources: " +
                _dragSessionDetector.ObserverRegistrationDiagnostics);
        }

        var probeMonitor = _primaryMonitor
            ?? throw new InvalidOperationException("The primary monitor was unavailable for Smart probe verification.");
        await _dragDropService.RunVerificationProbeSmokeAsync(
            new DragScreenPoint(
                probeMonitor.Left + probeMonitor.Width / 2,
                probeMonitor.Top + probeMonitor.Height / 2),
            cancellationToken);

        await Task.Delay(300, cancellationToken);
        CollectReleasedResources();
        var before = CaptureResources();

        for (var index = 0; index < cycles; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExerciseLifecycle();
            if (index % 10 == 9)
            {
                await Task.Delay(16, cancellationToken);
            }
        }

        _stateMachine.Restore(original.TemporaryItemCount);
        await Task.Delay(500, cancellationToken);
        CollectReleasedResources();
        var after = CaptureResources();
        var metrics = new OverlayLifecycleMetrics(
            cycles,
            _windows.Count,
            _activationHosts.Count,
            after.HandleCount - before.HandleCount,
            (long)after.GdiObjects - before.GdiObjects,
            (long)after.UserObjects - before.UserObjects,
            after.PrivateBytes - before.PrivateBytes,
            _windows.All(window => !window.HasActiveFrameSubscription),
            geometryStressCycles,
            regionFailures,
            idleTopEdgePassThrough,
            wakeModeSwitchVerified,
            smartObserverRegistered,
            compactVisualTargetDiscoverable,
            expandedVisualTargetDiscoverable);

        if (metrics.HandleDelta > 96 || metrics.GdiObjectDelta > 48 || metrics.UserObjectDelta > 48 ||
            metrics.PrivateBytesDelta > 192L * 1024 * 1024 || !metrics.NoContinuousFrameSubscription)
        {
            throw new InvalidOperationException($"Overlay lifecycle smoke exceeded its resource bounds: {metrics}.");
        }

        _logger.LogInformation(
            "Overlay lifecycle smoke passed {Cycles} cycles with handle delta {HandleDelta}, GDI delta {GdiDelta}, USER delta {UserDelta}, and private-byte delta {PrivateByteDelta}.",
            metrics.Cycles,
            metrics.HandleDelta,
            metrics.GdiObjectDelta,
            metrics.UserObjectDelta,
            metrics.PrivateBytesDelta);
        return metrics;
    }

    public void VerifyLocalizedResources()
    {
        if (_windows.Count == 0)
        {
            throw new InvalidOperationException("Overlay localization cannot be verified before initialization.");
        }

        foreach (var window in _windows)
        {
            window.VerifyLocalizedResources();
        }
    }

    public async Task<VisibleOverlayDropSmokeMetrics> RunVisibleOverlayDropSmokeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var testRoot = Path.Combine(Path.GetTempPath(), $"DropSpace-visible-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var compactAnchor = Path.Combine(testRoot, "compact-anchor.txt");
        var compactDrop = Path.Combine(testRoot, "compact-drop.txt");
        var expandedDrop = Path.Combine(testRoot, "expanded-drop.txt");
        await File.WriteAllTextAsync(compactAnchor, "anchor", cancellationToken);
        await File.WriteAllTextAsync(compactDrop, "compact", cancellationToken);
        await File.WriteAllTextAsync(expandedDrop, "expanded", cancellationToken);
        var baselineCount = _mainViewModel.SpaceItemCount;
        await _mainViewModel.NavigateAsync("Space", cancellationToken);

        try
        {
            if (await _mainViewModel.AddPathsAsync([compactAnchor], cancellationToken) != 1)
            {
                throw new InvalidOperationException("The visible-drop anchor could not be added.");
            }

            await _viewModel.RefreshRecentItemsAsync(cancellationToken);
            await Task.Delay(500, cancellationToken);
            var activeWindow = GetActiveWindow();
            await EnsureVisibleNativeDropTargetAsync(activeWindow, cancellationToken);
            await activeWindow.RunSyntheticCfHDropAsync([compactDrop], cancellationToken);
            await _viewModel.RefreshRecentItemsAsync(cancellationToken);
            var compactDropAccepted = _mainViewModel.Items.Any(item =>
                string.Equals(item.Item.File?.OriginalPath, compactDrop, StringComparison.OrdinalIgnoreCase));
            if (!compactDropAccepted || _viewModel.Snapshot.State != OverlayState.Compact)
            {
                throw new InvalidOperationException("Compact visible Overlay CF_HDROP did not settle back to Compact.");
            }

            await _viewModel.ExpandAsync(cancellationToken);
            await Task.Delay(500, cancellationToken);
            await EnsureVisibleNativeDropTargetAsync(activeWindow, cancellationToken);
            await activeWindow.RunSyntheticCfHDropAsync([expandedDrop], cancellationToken);
            await _viewModel.RefreshRecentItemsAsync(cancellationToken);
            var expandedDropAccepted = _mainViewModel.Items.Any(item =>
                string.Equals(item.Item.File?.OriginalPath, expandedDrop, StringComparison.OrdinalIgnoreCase));
            var expandedStayedOpen = _viewModel.Snapshot.State == OverlayState.Expanded;
            if (!expandedDropAccepted || !expandedStayedOpen)
            {
                throw new InvalidOperationException("Expanded visible Overlay CF_HDROP failed or collapsed the Expanded view.");
            }

            return new VisibleOverlayDropSmokeMetrics(
                compactDropAccepted,
                expandedDropAccepted,
                expandedStayedOpen,
                _mainViewModel.SpaceItemCount - baselineCount);
        }
        finally
        {
            foreach (var path in new[] { compactAnchor, compactDrop, expandedDrop })
            {
                var card = _mainViewModel.Items.FirstOrDefault(item =>
                    string.Equals(item.Item.File?.OriginalPath, path, StringComparison.OrdinalIgnoreCase));
                if (card is not null)
                {
                    await _mainViewModel.RemoveAsync(card, CancellationToken.None);
                }
            }

            foreach (var path in new[] { compactAnchor, compactDrop, expandedDrop })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: false);
            }
        }
    }

    private async Task EnsureVisibleNativeDropTargetAsync(
        OverlayWindow window,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (window.TryEnsureNativeDropTargetForSmoke())
            {
                return;
            }

            // Projection refresh and state publication are serialized through the dispatcher,
            // while this smoke runs on the same UI thread. Re-apply the current snapshot between
            // bounded polls so a just-published Compact/Expanded state can complete its native
            // show/registration transition before the synthetic OLE call.
            ApplySnapshot(_viewModel.Snapshot);
            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException(
            $"The visible Overlay OLE target did not become registered within the bounded smoke wait. " +
            $"{window.NativeVisibilityDiagnostics}.");
    }

    public async Task<ProjectionDeletionStressMetrics> RunProjectionDeletionStressAsync(
        int cycles,
        CancellationToken cancellationToken = default)
    {
        if (cycles is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(cycles));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        var unhandledBefore = _crashDiagnostics.UnhandledCount;
        var unobservedBefore = _crashDiagnostics.UnobservedTaskCount;
        var testRoot = Path.Combine(Path.GetTempPath(), $"DropSpace-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        var externalSentinel = Path.Combine(testRoot, "external-user-file.txt");
        await File.WriteAllTextAsync(externalSentinel, "DropSpace projection stress sentinel", cancellationToken);
        var baselineCount = _mainViewModel.SpaceItemCount;
        await _mainViewModel.NavigateAsync("Space", cancellationToken);

        try
        {
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var accepted = await _mainViewModel.AddPathsAsync([externalSentinel], cancellationToken);
                if (accepted != 1)
                {
                    throw new InvalidOperationException($"Projection stress add failed at cycle {cycle}.");
                }

                await _viewModel.RefreshRecentItemsAsync(cancellationToken);
                await _viewModel.ExpandAsync(cancellationToken);
                var card = _viewModel.RecentItems.FirstOrDefault(item =>
                    string.Equals(item.Item.File?.OriginalPath, externalSentinel, StringComparison.OrdinalIgnoreCase));
                if (card is null)
                {
                    throw new InvalidOperationException($"Overlay projection did not expose the stress item at cycle {cycle}.");
                }

                if (cycle % 2 == 0)
                {
                    await _viewModel.RemoveAsync(card, cancellationToken);
                }
                else
                {
                    var mainCard = _mainViewModel.Items.FirstOrDefault(item => item.Id == card.Id)
                        ?? throw new InvalidOperationException($"Main projection did not expose the stress item at cycle {cycle}.");
                    await _mainViewModel.RemoveAsync(mainCard, cancellationToken);
                }
                await _viewModel.RefreshRecentItemsAsync(cancellationToken);
                if (_mainViewModel.SpaceItemCount != baselineCount ||
                    _mainViewModel.Items.Any(item => item.Id == card.Id) ||
                    _viewModel.RecentItems.Any(item => item.Id == card.Id))
                {
                    throw new InvalidOperationException($"Temporary Space projections diverged after deletion at cycle {cycle}.");
                }

                if (!File.Exists(externalSentinel))
                {
                    throw new InvalidOperationException("Removing a DropSpace reference deleted the external sentinel file.");
                }

                if (cycle % 10 == 9)
                {
                    await Task.Delay(1, cancellationToken);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }

            await Task.Delay(100, cancellationToken);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var metrics = new ProjectionDeletionStressMetrics(
                cycles,
                _mainViewModel.SpaceItemCount,
                _viewModel.RecentItems.Count,
                _crashDiagnostics.UnhandledCount - unhandledBefore,
                _crashDiagnostics.UnobservedTaskCount - unobservedBefore,
                File.Exists(externalSentinel));
            if (metrics.UnhandledExceptionDelta != 0 ||
                metrics.UnobservedTaskExceptionDelta != 0 ||
                !metrics.ExternalSentinelPreserved)
            {
                throw new InvalidOperationException($"Projection deletion stress reported a failure: {metrics}.");
            }

            _logger.LogInformation(
                "Main Window + Expanded Overlay deletion stress passed {Cycles} cycles: authoritative count {Count}, unhandled delta {UnhandledDelta}, unobserved task delta {UnobservedDelta}, external sentinel preserved={SentinelPreserved}.",
                metrics.Cycles,
                metrics.FinalSpaceItemCount,
                metrics.UnhandledExceptionDelta,
                metrics.UnobservedTaskExceptionDelta,
                metrics.ExternalSentinelPreserved);
            return metrics;
        }
        finally
        {
            if (File.Exists(externalSentinel))
            {
                File.Delete(externalSentinel);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.SnapshotChanged -= OnSnapshotChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _mainViewModel.OverlayPlacementEditRequested -= OnOverlayPlacementEditRequested;
        _foregroundWindowMonitor.ForegroundChanged -= OnForegroundChanged;
        _dragSessionDetector.CandidateStarted -= OnSmartDragCandidateStarted;
        _dragSessionDetector.VerifiedFileDragStarted -= OnSmartVerifiedFileDragStarted;
        _dragSessionDetector.CandidateEnded -= OnSmartDragCandidateEnded;
        _dragSessionDetector.PlacementEditEscapeRequested -= OnPlacementEditEscapeRequested;
        _dragSessionDetector.SetPlacementEditing(false);
        ResumePlacementSuppressedWindows();
        _dragDropService.CancelVerificationProbe(_activeSmartSessionId);
        _activeSmartSessionId = 0;
        _dragSessionDetector.SetMode(FileDragWakeMode.Disabled);
        _quickPanelHotkey.Invoked -= OnQuickPanelHotkeyInvoked;
        _ = _quickPanelHotkey.StopAsync();
        if (_displayTopologyWatcher is not null)
        {
            _displayTopologyWatcher.Changed -= OnDisplayTopologyChanged;
            _displayTopologyWatcher.Dispose();
            _displayTopologyWatcher = null;
        }
        _foregroundWindowMonitor.Dispose();
        foreach (var window in _windows)
        {
            window.PlacementCommitted -= OnPlacementCommitted;
            window.PlacementCancelled -= OnPlacementCancelled;
            window.CloseForShutdown();
        }

        _windows.Clear();
        foreach (var host in _activationHosts)
        {
            host.DisplayTopologyChanged -= OnDisplayTopologyChanged;
            host.Dispose();
        }

        _activationHosts.Clear();
        _viewModel.Dispose();
        _disposed = true;
    }

    private void OnSnapshotChanged(object? sender, OverlaySnapshot snapshot)
    {
        _logger.LogInformation(
            "Overlay state transition: {State}, temporary item count {TemporaryItemCount}, monitor {MonitorId}, revision {Revision}.",
            snapshot.State,
            snapshot.TemporaryItemCount,
            _viewModel.ActiveMonitorId ?? "unselected",
            snapshot.Revision);
        ApplySnapshot(snapshot);
    }

    private void OnForegroundChanged(object? sender, EventArgs args) => ApplySnapshot(_viewModel.Snapshot);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(OverlayViewModel.MonitorPreference) && _primaryMonitor is not null)
        {
            if (_viewModel.MonitorPreference == OverlayMonitorPreference.Primary)
            {
                _viewModel.SetActiveMonitor(_primaryMonitor.Id);
            }

            ApplySnapshot(_viewModel.Snapshot);
        }
        else if (args.PropertyName == nameof(OverlayViewModel.ActiveMonitorId))
        {
            ApplySnapshot(_viewModel.Snapshot);
        }
        else if (args.PropertyName == nameof(OverlayViewModel.FileDragWakeMode))
        {
            ConfigureWakeMode(_viewModel.FileDragWakeMode);
            ApplySnapshot(_viewModel.Snapshot);
        }
        else if (args.PropertyName == nameof(OverlayViewModel.PlacementMode))
        {
            ApplySnapshot(_viewModel.Snapshot);
        }
        else if (args.PropertyName == nameof(OverlayViewModel.QuickPanelHotkey))
        {
            _ = RestartQuickPanelHotkeyAsync(_viewModel.QuickPanelHotkey);
        }
        else if (args.PropertyName == nameof(OverlayViewModel.SmartDragExcludedProcesses))
        {
            _dragSessionDetector.SetExcludedProcesses(_viewModel.SmartDragExcludedProcesses);
        }
    }

    private void OnOverlayPlacementEditRequested(object? sender, string monitorId)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }

            var window = _windows.FirstOrDefault(candidate =>
                string.Equals(candidate.MonitorId, monitorId, StringComparison.Ordinal));
            if (window is null)
            {
                _logger.LogWarning("The requested placement monitor {MonitorId} is not currently available.", monitorId);
                return;
            }

            if (_placementEditingWindow == window)
            {
                return;
            }

            if (!_mainViewModel.CanPersistOverlayPlacement(monitorId))
            {
                _logger.LogWarning(
                    "Placement edit rejected on runtime-only monitor {MonitorId}; a persistent display identity is required.",
                    monitorId);
                return;
            }

            if (_placementEditingWindow is not null && _placementEditingWindow != window)
            {
                _placementEditingWindow.CancelPlacementEdit();
            }

            _activeDragOwner = DragTargetOwner.None;
            CompleteSmartDetectorSession();
            _viewModel.CancelDrag();
            foreach (var host in _activationHosts)
            {
                host.SetEnabled(false);
            }

            _placementInputRestoreMode = _configuredWakeMode ?? _viewModel.FileDragWakeMode;
            _placementEditingWindow = window;
            // Reset the serialized drag policy before arming the edit. The edit still needs the
            // Smart observer's global Escape hook, so restart it after the old session is gone.
            _dragSessionDetector.SetMode(FileDragWakeMode.Disabled);
            _dragSessionDetector.SetPlacementEditing(true);
            // The overlay remains no-activate during the edit, so use the observer's global
            // Escape hook even when the user's configured wake mode is Classic or Disabled.
            _dragSessionDetector.SetMode(FileDragWakeMode.SmartExperimental);

            var placement = _viewModel.GetOverlayPlacement(monitorId);
            foreach (var candidate in _windows)
            {
                if (!ReferenceEquals(candidate, window))
                {
                    candidate.SuspendForPlacementEdit();
                }
            }
            var projected = window.GetProjectedPlacement(placement);
            window.BeginPlacementEdit(placement.CustomCoordinates, projected);
            _logger.LogInformation(
                "Dynamic Island placement edit armed on monitor {MonitorId}; Smart Drag candidate creation is suppressed until the edit ends.",
                monitorId);
        });
    }

    private void OnPlacementEditEscapeRequested(object? sender, EventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_placementEditingWindow is null || _disposed)
            {
                return;
            }

            _placementEditingWindow.CancelPlacementEdit();
        });
    }

    private async void OnPlacementCommitted(object? sender, OverlayPlacementEditEventArgs args)
    {
        if (sender is not OverlayWindow window || _disposed)
        {
            return;
        }

        _placementEditingWindow = null;
        ResumePlacementSuppressedWindows();
        _dragSessionDetector.SetPlacementEditing(false);
        RestorePlacementInputMode();
        try
        {
            await _mainViewModel.SetCustomOverlayPlacementAsync(
                window.MonitorId,
                args.Placement.X,
                args.Placement.Y);
            ApplySnapshot(_viewModel.Snapshot);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Dynamic Island placement could not be persisted on monitor {MonitorId}; the previous settings remain active.",
                window.MonitorId);
            ApplySnapshot(_viewModel.Snapshot);
        }
    }

    private void OnPlacementCancelled(object? sender, EventArgs args)
    {
        if (sender is not OverlayWindow window)
        {
            return;
        }

        if (_placementEditingWindow == window)
        {
            _placementEditingWindow = null;
        }

        _dragSessionDetector.SetPlacementEditing(false);
        ResumePlacementSuppressedWindows();
        RestorePlacementInputMode();
        ApplySnapshot(_viewModel.Snapshot);
        _logger.LogInformation("Dynamic Island placement edit cancelled on monitor {MonitorId}.", window.MonitorId);
    }

    private void RestorePlacementInputMode()
    {
        var restoreMode = _placementInputRestoreMode;
        _placementInputRestoreMode = null;
        if (restoreMode is { } mode && mode != FileDragWakeMode.SmartExperimental)
        {
            _dragSessionDetector.SetMode(mode);
        }
    }

    private void ResumePlacementSuppressedWindows()
    {
        foreach (var window in _windows)
        {
            window.ResumeAfterPlacementEdit();
        }
    }

    private async Task RestartQuickPanelHotkeyAsync(string gesture)
    {
        try
        {
            if (!await _quickPanelHotkey.TryStartAsync(gesture).ConfigureAwait(false))
            {
                _logger.LogWarning("Quick Panel retained its previous registered hotkey after a registration conflict.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Quick Panel hotkey restart failed safely.");
        }
    }

    private void OnQuickPanelHotkeyInvoked(object? sender, EventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed)
            {
                return;
            }
            _activeDragOwner = DragTargetOwner.None;
            _dragDropService.CancelVerificationProbe(_activeSmartSessionId);
            _activeSmartSessionId = 0;
            _stateMachine.OpenQuickPanel();
            _logger.LogInformation("Quick Panel opened from the registered global hotkey.");
        });
    }

    private void ApplySnapshot(OverlaySnapshot snapshot)
    {
        if (_primaryMonitor is null)
        {
            return;
        }

        // An explicit smart-drag session follows the pointer display even when passive Compact
        // presentation is pinned to Primary. The user's monitor preference resumes after Drop or
        // cancellation; only the temporary interaction is pointer-local.
        var smartPointerDisplay = _viewModel.FileDragWakeMode == FileDragWakeMode.SmartExperimental &&
                                  (snapshot.State is OverlayState.DragApproaching or OverlayState.DragReady);
        var primaryOnly = _viewModel.MonitorPreference == OverlayMonitorPreference.Primary &&
                          !smartPointerDisplay;
        var activeMonitorId = primaryOnly ? _primaryMonitor.Id : _viewModel.ActiveMonitorId;
        foreach (var host in _activationHosts)
        {
            var monitorEnabled = !primaryOnly || host.MonitorId == _primaryMonitor.Id;
            var visualSurfaceOwnsStableInput = snapshot.State is OverlayState.Compact or OverlayState.Expanded;
            var activationEnabled = _activeDragOwner == DragTargetOwner.ActivationHost ||
                                    _activeDragOwner == DragTargetOwner.None && !visualSurfaceOwnsStableInput;
            host.SetEnabled(monitorEnabled && activationEnabled);
        }

        foreach (var window in _windows)
        {
            var activationEnabled = !primaryOnly || window.MonitorId == _primaryMonitor.Id;
            window.ApplySnapshot(
                snapshot,
                string.Equals(window.MonitorId, activeMonitorId, StringComparison.Ordinal),
                activationEnabled,
                _viewModel.FileDragWakeMode,
                _viewModel.GetOverlayPlacement(window.MonitorId));
        }
    }

    private void CreateMonitorSurfaces()
    {
        var monitors = _monitorLayout.GetMonitors();
        _primaryMonitor = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        var visualCallbacks = new DragActivationCallbacks(
            OnVisibleDragApproaching,
            OnVisibleDragReadyChanged,
            OnVisibleDragLeft,
            OnVisibleDroppedAsync,
            OnVisibleOwnedDroppedAsync);
        foreach (var monitor in monitors)
        {
            var window = new OverlayWindow(
                _viewModel,
                _strings,
                monitor,
                _monitorLayout,
                _capabilities,
                _dragDropService,
                _quickActionDialog,
                visualCallbacks,
                _openMainWindow ?? throw new InvalidOperationException("The main-window callback is unavailable."),
                _loggerFactory.CreateLogger<OverlayWindow>());
            window.PlacementCommitted += OnPlacementCommitted;
            window.PlacementCancelled += OnPlacementCancelled;
            _windows.Add(window);
        }
    }

    private void ConfigureWakeMode(FileDragWakeMode mode, bool force = false)
    {
        if (!force && _configuredWakeMode == mode)
        {
            return;
        }

        foreach (var host in _activationHosts)
        {
            host.DisplayTopologyChanged -= OnDisplayTopologyChanged;
            host.Dispose();
        }

        _activationHosts.Clear();
        _dragDropService.CancelVerificationProbe(_activeSmartSessionId);
        _activeSmartSessionId = 0;
        if (_activeDragOwner == DragTargetOwner.SmartDetector)
        {
            _viewModel.CancelDrag();
            _activeDragOwner = DragTargetOwner.None;
        }

        _dragSessionDetector.SetMode(mode);
        _configuredWakeMode = mode;
        if (mode == FileDragWakeMode.ClassicTopEdge)
        {
            var callbacks = new DragActivationCallbacks(
                OnDragApproaching,
                OnDragReadyChanged,
                OnDragLeft,
                OnDroppedAsync,
                OnOwnedDroppedAsync);
            foreach (var monitor in _monitorLayout.GetMonitors())
            {
                var host = _dragDropService.CreateActivationHost(monitor, callbacks);
                _activationHosts.Add(host);
            }
        }

        _logger.LogInformation(
            "File drag wake mode applied: {Mode}; classic idle activation host count {HostCount}; smart observer status: {ObserverStatus}.",
            mode,
            _activationHosts.Count,
            _dragSessionDetector.ObserverRegistrationDiagnostics);
    }

    private void OnSmartDragCandidateStarted(object? sender, DragSessionCandidate candidate)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _viewModel.FileDragWakeMode != FileDragWakeMode.SmartExperimental)
            {
                return;
            }

            if (_activeSmartSessionId != 0 && _activeSmartSessionId != candidate.SessionId)
            {
                _dragDropService.CancelVerificationProbe(_activeSmartSessionId);
            }

            _activeSmartSessionId = candidate.SessionId;
            _activeSmartSessionPoint = candidate.Point;
            _logger.LogInformation(
                "Smart drag candidate {SessionId} is awaiting positive OLE file evidence on monitor {MonitorId}; source={Source}, evidenceLevel={EvidenceLevel}, requiresOleVerification={RequiresOleVerification}; the visual target remains hidden.",
                candidate.SessionId,
                candidate.MonitorId,
                candidate.Source,
                candidate.EvidenceLevel,
                candidate.RequiresOleVerification);
            if (!candidate.RequiresOleVerification)
            {
                _logger.LogError(
                    "Smart drag candidate {SessionId} did not require OLE verification; refusing to reveal the visual target.",
                    candidate.SessionId);
                _dragSessionDetector.NotifyProbeTimedOut(candidate.SessionId, candidate.Point);
                return;
            }

            try
            {
                _dragDropService.StartVerificationProbe(
                    candidate.SessionId,
                    candidate.Point,
                    OnSmartProbeCompleted,
                    () => !_disposed &&
                          _viewModel.FileDragWakeMode == FileDragWakeMode.SmartExperimental &&
                          _activeSmartSessionId == candidate.SessionId &&
                          _dragSessionDetector.IsVerificationPending(candidate.SessionId));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Smart OLE verification probe could not start for session {SessionId}; the visual target remains hidden.",
                    candidate.SessionId);
                _dragSessionDetector.NotifyProbeTimedOut(candidate.SessionId, candidate.Point);
            }
        });
    }

    private void OnSmartVerifiedFileDragStarted(object? sender, DragSessionCandidate candidate)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed ||
                _viewModel.FileDragWakeMode != FileDragWakeMode.SmartExperimental ||
                _activeSmartSessionId != candidate.SessionId)
            {
                return;
            }

            _activeSmartSessionPoint = candidate.Point;
            _activeDragOwner = DragTargetOwner.SmartDetector;
            _viewModel.BeginDragApproach(candidate.MonitorId);
            _logger.LogInformation(
                "Smart drag candidate {SessionId} passed positive OLE file evidence and revealed the visual target on monitor {MonitorId}: evidenceLevel={EvidenceLevel}, payloadConfidence={PayloadConfidence}.",
                candidate.SessionId,
                candidate.MonitorId,
                candidate.EvidenceLevel,
                candidate.PayloadConfidence);
        });
    }

    private void OnSmartDragCandidateEnded(object? sender, long sessionId)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _activeSmartSessionId != sessionId)
            {
                return;
            }

            _dragDropService.CancelVerificationProbe(sessionId);
            _activeSmartSessionId = 0;
            if (_activeDragOwner != DragTargetOwner.SmartDetector)
            {
                return;
            }

            _viewModel.CancelDrag();
            _activeDragOwner = DragTargetOwner.None;
            ApplySnapshot(_viewModel.Snapshot);
            _logger.LogInformation(
                "Smart drag candidate {SessionId} ended after the Smart visual ownership session was active.",
                sessionId);
        });
    }

    private void OnSmartProbeCompleted(OleDragProbeResult result)
    {
        if (_disposed || result.SessionId != _activeSmartSessionId)
        {
            return;
        }

        _logger.LogInformation(
            "Smart OLE probe completed for session {SessionId}: outcome={Outcome}, classification={Classification}, elapsed={ElapsedMilliseconds:F1}ms.",
            result.SessionId,
            result.Outcome,
            result.Classification.Kind,
            result.Elapsed.TotalMilliseconds);
        _dragSessionDetector.RecordProbeLatency(result.Elapsed);
        switch (result.Outcome)
        {
            case OleDragProbeOutcome.VerifiedFile:
                _dragSessionDetector.NotifyProbeVerified(result.SessionId, result.Point);
                break;
            case OleDragProbeOutcome.Rejected:
                _dragSessionDetector.NotifyProbeRejected(result.SessionId, result.Point);
                break;
            case OleDragProbeOutcome.TimedOut:
                _dragSessionDetector.NotifyProbeTimedOut(result.SessionId, result.Point);
                break;
        }
    }

    private void OnDragApproaching(string monitorId)
    {
        _activeDragOwner = DragTargetOwner.ActivationHost;
        _logger.LogInformation(
            "Visual overlay reveal requested by drag activation on monitor {MonitorId}.",
            monitorId);
        _viewModel.BeginDragApproach(monitorId);
    }

    private void OnDragReadyChanged(string monitorId, bool ready)
    {
        if (!string.Equals(_viewModel.ActiveMonitorId, monitorId, StringComparison.Ordinal))
        {
            _viewModel.BeginDragApproach(monitorId);
        }

        _viewModel.SetDragReady(ready);
    }

    private void OnDragLeft(string monitorId)
    {
        if (string.Equals(_viewModel.ActiveMonitorId, monitorId, StringComparison.Ordinal))
        {
            _viewModel.CancelDrag();
        }

        _activeDragOwner = DragTargetOwner.None;
        CompleteSmartDetectorSession();
        ApplySnapshot(_viewModel.Snapshot);
    }

    private async Task OnDroppedAsync(string monitorId, IReadOnlyList<string> paths)
    {
        try
        {
            var accepted = await _viewModel.CompleteDropAsync(monitorId, paths);
            _logger.LogInformation(
                "Temporary Space activation-host drop completed on monitor {MonitorId}: offered {OfferedCount}, accepted {AcceptedCount}.",
                monitorId,
                paths.Count,
                accepted);
        }
        finally
        {
            _activeDragOwner = DragTargetOwner.None;
            CompleteSmartDetectorSession();
            ApplySnapshot(_viewModel.Snapshot);
        }
    }

    private async Task OnOwnedDroppedAsync(string monitorId, IReadOnlyList<string> paths)
    {
        try
        {
            await _viewModel.CompleteOwnedDropAsync(monitorId, paths, visibleTarget: false);
        }
        finally
        {
            _activeDragOwner = DragTargetOwner.None;
            CompleteSmartDetectorSession();
            ApplySnapshot(_viewModel.Snapshot);
        }
    }

    private void OnVisibleDragApproaching(string monitorId)
    {
        if (_activeSmartSessionId != 0)
        {
            _dragDropService.CancelVerificationProbe(_activeSmartSessionId);
            _dragSessionDetector.NotifyProbeVerified(
                _activeSmartSessionId,
                _activeSmartSessionPoint);
        }

        _activeDragOwner = DragTargetOwner.VisualOverlay;
        _logger.LogInformation(
            "Visible Overlay accepted direct drag ownership on monitor {MonitorId}; passive activation hosts are disabled until Drop/Leave.",
            monitorId);
        _viewModel.BeginVisibleDragApproach(monitorId);
    }

    private void OnVisibleDragReadyChanged(string monitorId, bool ready)
    {
        if (_activeDragOwner != DragTargetOwner.VisualOverlay)
        {
            OnVisibleDragApproaching(monitorId);
        }

        _viewModel.SetDragReady(ready);
    }

    private void OnVisibleDragLeft(string monitorId)
    {
        if ((_activeDragOwner is DragTargetOwner.VisualOverlay or DragTargetOwner.SmartDetector) &&
            string.Equals(_viewModel.ActiveMonitorId, monitorId, StringComparison.Ordinal))
        {
            _viewModel.CancelDrag();
        }

        _activeDragOwner = DragTargetOwner.None;
        CompleteSmartDetectorSession();
        ApplySnapshot(_viewModel.Snapshot);
    }

    private async Task OnVisibleDroppedAsync(string monitorId, IReadOnlyList<string> paths)
    {
        try
        {
            var accepted = await _viewModel.CompleteVisibleDropAsync(monitorId, paths);
            _logger.LogInformation(
                "Visible Overlay direct drop completed on monitor {MonitorId}: offered {OfferedCount}, accepted {AcceptedCount}, resulting state {State}.",
                monitorId,
                paths.Count,
                accepted,
                _viewModel.Snapshot.State);
        }
        finally
        {
            _activeDragOwner = DragTargetOwner.None;
            CompleteSmartDetectorSession();
            ApplySnapshot(_viewModel.Snapshot);
        }
    }

    private async Task OnVisibleOwnedDroppedAsync(string monitorId, IReadOnlyList<string> paths)
    {
        try
        {
            await _viewModel.CompleteOwnedDropAsync(monitorId, paths, visibleTarget: true);
        }
        finally
        {
            _activeDragOwner = DragTargetOwner.None;
            CompleteSmartDetectorSession();
            ApplySnapshot(_viewModel.Snapshot);
        }
    }

    private void CompleteSmartDetectorSession()
    {
        var sessionId = _activeSmartSessionId;
        if (sessionId == 0)
        {
            return;
        }

        _dragDropService.CancelVerificationProbe(sessionId);
        _activeSmartSessionId = 0;
        _dragSessionDetector.NotifyOleSessionCompleted(sessionId);
    }

    private void OnDisplayTopologyChanged(object? sender, EventArgs args)
    {
        if (_topologyRefreshPending || _disposed)
        {
            return;
        }

        _topologyRefreshPending = true;
        _dispatcher.TryEnqueue(() =>
        {
            _topologyRefreshPending = false;
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_placementEditingWindow is not null)
                {
                    _placementEditingWindow.CancelPlacementEdit();
                    _placementEditingWindow = null;
                    _dragSessionDetector.SetPlacementEditing(false);
                }
                ResumePlacementSuppressedWindows();

                foreach (var window in _windows)
                {
                    window.PlacementCommitted -= OnPlacementCommitted;
                    window.PlacementCancelled -= OnPlacementCancelled;
                    window.CloseForShutdown();
                }

                _windows.Clear();
                foreach (var host in _activationHosts)
                {
                    host.DisplayTopologyChanged -= OnDisplayTopologyChanged;
                    host.Dispose();
                }

                _activationHosts.Clear();
                CreateMonitorSurfaces();
                ConfigureWakeMode(_viewModel.FileDragWakeMode, force: true);
                if (_primaryMonitor is not null &&
                    !_windows.Any(window => string.Equals(
                        window.MonitorId,
                        _viewModel.ActiveMonitorId,
                        StringComparison.Ordinal)))
                {
                    _viewModel.SetActiveMonitor(_primaryMonitor.Id);
                }

                ApplySnapshot(_viewModel.Snapshot);
                _logger.LogInformation(
                    "Overlay surfaces rebuilt after a display-topology change; window count {WindowCount}, classic activation host count {HostCount}.",
                    _windows.Count,
                    _activationHosts.Count);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Display-topology refresh failed.");
            }
        });
    }

    private void ExerciseLifecycle()
    {
        _stateMachine.Restore(0);
        _stateMachine.BeginDragApproach();
        _stateMachine.SetDragReady(true);
        _stateMachine.CompleteDrop(1);
        _stateMachine.Expand();
        _stateMachine.Collapse();
        _stateMachine.SetTemporaryItemCount(0);
        _stateMachine.CompleteDismissal();
    }

    private VisibleWindowProbe ProbeActiveVisualCenter()
    {
        return GetActiveWindow().ProbeVisibleCenter();
    }

    private bool VerifyWakeModeSwitchOwnership(FileDragWakeMode originalMode)
    {
        try
        {
            ConfigureWakeMode(FileDragWakeMode.ClassicTopEdge, force: true);
            ApplySnapshot(_viewModel.Snapshot);
            var classicTargetOwned = _activationHosts.Count == _windows.Count &&
                                     _activationHosts.All(host => host.IsIdleTargetDiscoverable());

            ConfigureWakeMode(FileDragWakeMode.Disabled, force: true);
            ApplySnapshot(_viewModel.Snapshot);
            var disabledPassThrough = _activationHosts.Count == 0 &&
                                      _windows.All(window => window.ProbeIdleTopEdgePassThrough());
            return classicTargetOwned && disabledPassThrough;
        }
        finally
        {
            ConfigureWakeMode(originalMode, force: true);
            ApplySnapshot(_viewModel.Snapshot);
        }
    }

    private bool VerifySmartObserverRegistration(FileDragWakeMode originalMode)
    {
        try
        {
            ConfigureWakeMode(FileDragWakeMode.SmartExperimental, force: true);
            var registrationCompleted = _dragSessionDetector.WaitForObserverRegistration(
                TimeSpan.FromSeconds(5));
            return registrationCompleted &&
                   _dragSessionDetector.MouseObserverRegistered &&
                   (_dragSessionDetector.ObjectDragEventsRegistered ||
                    _dragSessionDetector.SystemDragEventsRegistered);
        }
        finally
        {
            if (originalMode != FileDragWakeMode.SmartExperimental)
            {
                ConfigureWakeMode(originalMode, force: true);
                ApplySnapshot(_viewModel.Snapshot);
            }
        }
    }

    private OverlayWindow GetActiveWindow()
    {
        var activeMonitorId = _viewModel.ActiveMonitorId ?? _primaryMonitor?.Id;
        return _windows.FirstOrDefault(candidate =>
                   string.Equals(candidate.MonitorId, activeMonitorId, StringComparison.Ordinal))
               ?? throw new InvalidOperationException("No active visual Overlay Window is available.");
    }

    private static void CollectReleasedResources()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static ResourceSnapshot CaptureResources()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ResourceSnapshot(
            process.HandleCount,
            GetGuiResources(process.Handle, 0),
            GetGuiResources(process.Handle, 1),
            process.PrivateMemorySize64);
    }

    private sealed record ResourceSnapshot(int HandleCount, uint GdiObjects, uint UserObjects, long PrivateBytes);

    private enum DragTargetOwner
    {
        None,
        ActivationHost,
        VisualOverlay,
        SmartDetector,
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);
}

public sealed record OverlayLifecycleMetrics(
    int Cycles,
    int WindowCount,
    int ActivationHostCount,
    int HandleDelta,
    long GdiObjectDelta,
    long UserObjectDelta,
    long PrivateBytesDelta,
    bool NoContinuousFrameSubscription,
    int GeometryStressCycles,
    long RegionFailureCount,
    bool IdleTopEdgePassThrough,
    bool WakeModeSwitchVerified,
    bool SmartObserverRegistered,
    bool CompactVisualTargetDiscoverable,
    bool ExpandedVisualTargetDiscoverable);

public sealed record ProjectionDeletionStressMetrics(
    int Cycles,
    int FinalSpaceItemCount,
    int FinalRecentItemCount,
    long UnhandledExceptionDelta,
    long UnobservedTaskExceptionDelta,
    bool ExternalSentinelPreserved);

public sealed record VisibleOverlayDropSmokeMetrics(
    bool CompactDropAccepted,
    bool ExpandedDropAccepted,
    bool ExpandedStayedOpen,
    int AddedItemCount);
