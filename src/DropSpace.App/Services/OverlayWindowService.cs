using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DropSpace.App.ViewModels;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed class OverlayWindowService : IDisposable
{
    private readonly OverlayViewModel _viewModel;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly ForegroundWindowMonitor _foregroundWindowMonitor;
    private readonly OverlayStateMachine _stateMachine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OverlayWindowService> _logger;
    private readonly List<OverlayWindow> _windows = [];
    private MonitorDescriptor? _primaryMonitor;
    private bool _disposed;

    public OverlayWindowService(
        OverlayViewModel viewModel,
        MonitorLayoutService monitorLayout,
        ForegroundWindowMonitor foregroundWindowMonitor,
        OverlayStateMachine stateMachine,
        ILoggerFactory loggerFactory)
    {
        _viewModel = viewModel;
        _monitorLayout = monitorLayout;
        _foregroundWindowMonitor = foregroundWindowMonitor;
        _stateMachine = stateMachine;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OverlayWindowService>();
    }

    public async Task InitializeAsync(Action openMainWindow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openMainWindow);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_windows.Count > 0)
        {
            return;
        }

        var monitors = _monitorLayout.GetMonitors();
        _primaryMonitor = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        foreach (var monitor in monitors)
        {
            _windows.Add(new OverlayWindow(
                _viewModel,
                monitor,
                _monitorLayout,
                openMainWindow,
                _loggerFactory.CreateLogger<OverlayWindow>()));
        }

        _viewModel.SnapshotChanged += OnSnapshotChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _foregroundWindowMonitor.ForegroundChanged += OnForegroundChanged;
        _foregroundWindowMonitor.Start();
        await _viewModel.InitializeAsync(_primaryMonitor.Id, cancellationToken);
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
            ExerciseLifecycle(original.DisplayMode);
        }

        await Task.Delay(300, cancellationToken);
        CollectReleasedResources();
        var before = CaptureResources();

        for (var index = 0; index < cycles; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExerciseLifecycle(original.DisplayMode);
            if (index % 10 == 9)
            {
                await Task.Delay(16, cancellationToken);
            }
        }

        _stateMachine.Restore(original.TemporaryItemCount, original.TargetDisplayMode);
        await Task.Delay(500, cancellationToken);
        CollectReleasedResources();
        var after = CaptureResources();
        var metrics = new OverlayLifecycleMetrics(
            cycles,
            _windows.Count,
            after.HandleCount - before.HandleCount,
            (long)after.GdiObjects - before.GdiObjects,
            (long)after.UserObjects - before.UserObjects,
            after.PrivateBytes - before.PrivateBytes,
            _windows.All(window => !window.HasActiveFrameSubscription));

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.SnapshotChanged -= OnSnapshotChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _foregroundWindowMonitor.ForegroundChanged -= OnForegroundChanged;
        _foregroundWindowMonitor.Dispose();
        foreach (var window in _windows)
        {
            window.CloseForShutdown();
        }

        _windows.Clear();
        _viewModel.Dispose();
        _disposed = true;
    }

    private void OnSnapshotChanged(object? sender, OverlaySnapshot snapshot) => ApplySnapshot(snapshot);

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
    }

    private void ApplySnapshot(OverlaySnapshot snapshot)
    {
        if (_primaryMonitor is null)
        {
            return;
        }

        var primaryOnly = _viewModel.MonitorPreference == OverlayMonitorPreference.Primary;
        var activeMonitorId = primaryOnly ? _primaryMonitor.Id : _viewModel.ActiveMonitorId;
        foreach (var window in _windows)
        {
            var activationEnabled = !primaryOnly || window.MonitorId == _primaryMonitor.Id;
            window.ApplySnapshot(
                snapshot,
                string.Equals(window.MonitorId, activeMonitorId, StringComparison.Ordinal),
                activationEnabled);
        }
    }

    private void ExerciseLifecycle(OverlayDisplayMode initialMode)
    {
        var alternateMode = initialMode == OverlayDisplayMode.DynamicIsland
            ? OverlayDisplayMode.Notch
            : OverlayDisplayMode.DynamicIsland;
        _stateMachine.Restore(0, initialMode);
        _stateMachine.RequestDisplayMode(alternateMode);
        _stateMachine.CompleteModeTransition();
        _stateMachine.RequestDisplayMode(initialMode);
        _stateMachine.CompleteModeTransition();
        _stateMachine.BeginDragApproach();
        _stateMachine.SetDragReady(true);
        _stateMachine.CompleteDrop(1);
        _stateMachine.Expand();
        _stateMachine.Collapse();
        _stateMachine.SetTemporaryItemCount(0);
        _stateMachine.CompleteDismissal();
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

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);
}

public sealed record OverlayLifecycleMetrics(
    int Cycles,
    int WindowCount,
    int HandleDelta,
    long GdiObjectDelta,
    long UserObjectDelta,
    long PrivateBytesDelta,
    bool NoContinuousFrameSubscription);
