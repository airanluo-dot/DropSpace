using System.ComponentModel;
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
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<OverlayWindow> _windows = [];
    private MonitorDescriptor? _primaryMonitor;
    private bool _disposed;

    public OverlayWindowService(
        OverlayViewModel viewModel,
        MonitorLayoutService monitorLayout,
        ForegroundWindowMonitor foregroundWindowMonitor,
        ILoggerFactory loggerFactory)
    {
        _viewModel = viewModel;
        _monitorLayout = monitorLayout;
        _foregroundWindowMonitor = foregroundWindowMonitor;
        _loggerFactory = loggerFactory;
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
}
