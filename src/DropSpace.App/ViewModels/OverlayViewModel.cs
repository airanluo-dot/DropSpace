using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Collections;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.ViewModels;

public sealed class OverlayViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _mainViewModel;
    private readonly OverlayStateMachine _stateMachine;
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<OverlayViewModel> _logger;
    private readonly SerializedProjectionRefreshCoordinator<ItemCardViewModel> _projectionRefresh;
    private OverlaySnapshot _snapshot;
    private AppSettings? _pendingSettings;
    private string? _activeMonitorId;
    private bool _disposed;

    public OverlayViewModel(
        MainViewModel mainViewModel,
        OverlayStateMachine stateMachine,
        DispatcherQueue dispatcher,
        IAppStringLocalizer strings,
        ILogger<OverlayViewModel> logger)
    {
        _mainViewModel = mainViewModel;
        _stateMachine = stateMachine;
        _dispatcher = dispatcher;
        _strings = strings;
        _logger = logger;
        _snapshot = stateMachine.Snapshot;
        _projectionRefresh = new SerializedProjectionRefreshCoordinator<ItemCardViewModel>(
            cancellationToken => _mainViewModel.GetRecentSpaceItemsAsync(5, cancellationToken),
            ApplyRecentItemsAsync);
        _mainViewModel.UiSettingsPreflightAsync = ApplyUiSettingsAsync;
    }

    public event EventHandler<OverlaySnapshot>? SnapshotChanged;

    public ObservableCollection<ItemCardViewModel> RecentItems { get; } = [];

    public OverlaySnapshot Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetProperty(ref _snapshot, value))
            {
                OnPropertyChanged(nameof(IsDragPromptVisible));
                OnPropertyChanged(nameof(IsCompactVisible));
                OnPropertyChanged(nameof(IsExpandedVisible));
                OnPropertyChanged(nameof(CompactTitle));
                OnPropertyChanged(nameof(DragTitle));
                OnPropertyChanged(nameof(DragSubtitle));
                OnPropertyChanged(nameof(IsExpandedDropTargetActive));
            }
        }
    }

    public string? ActiveMonitorId
    {
        get => _activeMonitorId;
        private set => SetProperty(ref _activeMonitorId, value);
    }

    public OverlayMonitorPreference MonitorPreference =>
        (_pendingSettings ?? _mainViewModel.Settings).OverlayMonitor;

    public OverlayMotionPreference MotionPreference =>
        (_pendingSettings ?? _mainViewModel.Settings).OverlayMotion;

    public FileDragWakeMode FileDragWakeMode =>
        (_pendingSettings ?? _mainViewModel.Settings).FileDragWakeMode;

    public OverlayPlacementMode PlacementMode =>
        (_pendingSettings ?? _mainViewModel.Settings).OverlayPlacementMode;

    public string QuickPanelHotkey =>
        (_pendingSettings ?? _mainViewModel.Settings).QuickPanelHotkey;

    public IReadOnlyList<string> SmartDragExcludedProcesses =>
        (_pendingSettings ?? _mainViewModel.Settings).SmartDragExcludedProcesses;

    public OverlayMonitorPlacement GetOverlayPlacement(string monitorId) =>
        _mainViewModel.GetOverlayPlacement(monitorId, _pendingSettings);

    public bool IsDragPromptVisible => Snapshot.State is OverlayState.DragApproaching or OverlayState.DragReady;

    public bool IsCompactVisible => Snapshot.State == OverlayState.Compact;

    public bool IsExpandedVisible => Snapshot.State == OverlayState.Expanded;

    public bool IsExpandedDropTargetActive => Snapshot.ExpandedDropActive;

    public string CompactTitle => Snapshot.TemporaryItemCount switch
    {
        0 => _strings.Get("OverlayTitle"),
        1 when RecentItems.FirstOrDefault() is { } item => item.Title,
        _ => _strings.Format("OverlayItemCount", Snapshot.TemporaryItemCount),
    };

    public string DragTitle => Snapshot.State == OverlayState.DragReady
        ? _strings.Get("OverlayDropTitle")
        : FileDragWakeMode == DropSpace.Core.Models.FileDragWakeMode.ClassicTopEdge
            ? _strings.Get("OverlayClassicDragTitle")
            : _strings.Get("OverlaySmartDragTitle");

    public string DragSubtitle => Snapshot.State == OverlayState.DragReady
        ? _strings.Get("OverlayDropSubtitle")
        : _strings.Get("OverlayDragSubtitle");

    public async Task InitializeAsync(string initialMonitorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialMonitorId);
        ActiveMonitorId = initialMonitorId;
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        _mainViewModel.SpaceProjectionChanged += OnSpaceProjectionChanged;
        _stateMachine.Changed += OnStateChanged;
        _stateMachine.Restore(_mainViewModel.SpaceItemCount);
        await RefreshRecentItemsAsync(cancellationToken);
    }

    public void BeginDragApproach(string monitorId)
    {
        ActiveMonitorId = monitorId;
        _stateMachine.BeginDragApproach();
    }

    public void BeginVisibleDragApproach(string monitorId)
    {
        ActiveMonitorId = monitorId;
        _stateMachine.BeginVisibleDrag();
    }

    public void SetActiveMonitor(string monitorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        ActiveMonitorId = monitorId;
    }

    public void SetDragReady(bool ready) => _stateMachine.SetDragReady(ready);

    public void CancelDrag() => _stateMachine.CancelDrag();

    public async Task<int> CompleteDropAsync(
        string monitorId,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ActiveMonitorId = monitorId;
        var accepted = await _mainViewModel.AddPathsAsync(paths, cancellationToken);
        await RefreshRecentItemsAsync(cancellationToken);
        _stateMachine.CompleteDrop(_mainViewModel.SpaceItemCount);
        return accepted;
    }

    public async Task<int> CompleteVisibleDropAsync(
        string monitorId,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ActiveMonitorId = monitorId;
        var accepted = await _mainViewModel.AddPathsAsync(paths, cancellationToken);
        await RefreshRecentItemsAsync(cancellationToken);
        _stateMachine.CompleteVisibleDrop(_mainViewModel.SpaceItemCount);
        return accepted;
    }

    public async Task<int> CompleteOwnedDropAsync(
        string monitorId,
        IEnumerable<string> paths,
        bool visibleTarget,
        CancellationToken cancellationToken = default)
    {
        ActiveMonitorId = monitorId;
        var accepted = await _mainViewModel.AddOwnedPathsBatchAsync(
            paths,
            null,
            "ole-virtual-file-drop",
            2L * 1024 * 1024 * 1024,
            cancellationToken);
        await RefreshRecentItemsAsync(cancellationToken);
        if (visibleTarget)
        {
            _stateMachine.CompleteVisibleDrop(_mainViewModel.SpaceItemCount);
        }
        else
        {
            _stateMachine.CompleteDrop(_mainViewModel.SpaceItemCount);
        }
        return accepted;
    }

    public async Task CompleteVisibleTextDropAsync(
        string monitorId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ActiveMonitorId = monitorId;
        await _mainViewModel.AddTextToSpaceAsync(text, "overlay-text-url-drop", cancellationToken: cancellationToken);
        await RefreshRecentItemsAsync(cancellationToken);
        _stateMachine.CompleteVisibleDrop(_mainViewModel.SpaceItemCount);
    }

    public async Task ExpandAsync(CancellationToken cancellationToken = default)
    {
        await RefreshRecentItemsAsync(cancellationToken);
        _stateMachine.Expand();
    }

    public void Collapse() => _stateMachine.Collapse();

    public void CompleteDismissal() => _stateMachine.CompleteDismissal();

    private Task ApplyUiSettingsAsync(AppSettings candidate, CancellationToken cancellationToken)
    {
        candidate.Validate();
        return _dispatcher.HasThreadAccess
            ? ApplyUiSettingsOnDispatcherAsync(candidate, cancellationToken)
            : _dispatcher.EnqueueAsync(() => ApplyUiSettingsOnDispatcherAsync(candidate, cancellationToken));
    }

    private Task ApplyUiSettingsOnDispatcherAsync(
        AppSettings candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pendingSettings = candidate;
        OnPropertyChanged(nameof(MonitorPreference));
        OnPropertyChanged(nameof(MotionPreference));
        OnPropertyChanged(nameof(FileDragWakeMode));
        OnPropertyChanged(nameof(PlacementMode));
        OnPropertyChanged(nameof(QuickPanelHotkey));
        OnPropertyChanged(nameof(SmartDragExcludedProcesses));
        return Task.CompletedTask;
    }

    public async Task OpenAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        await _mainViewModel.OpenAsync(card, cancellationToken);
    }

    public async Task TogglePinAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        await _mainViewModel.TogglePinAsync(card, cancellationToken);
        await RefreshRecentItemsAsync(cancellationToken);
    }

    public async Task RemoveAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        await _mainViewModel.RemoveAsync(card, cancellationToken);
        await RefreshRecentItemsAsync(cancellationToken);
    }

    public async Task RefreshRecentItemsAsync(CancellationToken cancellationToken = default)
    {
        await _projectionRefresh.RequestAsync(_mainViewModel.SpaceRevision, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        _mainViewModel.SpaceProjectionChanged -= OnSpaceProjectionChanged;
        _stateMachine.Changed -= OnStateChanged;
        if (_mainViewModel.UiSettingsPreflightAsync == ApplyUiSettingsAsync)
        {
            _mainViewModel.UiSettingsPreflightAsync = null;
        }

        _projectionRefresh.Dispose();
        _disposed = true;
    }

    private Task ApplyRecentItemsAsync(
        IReadOnlyList<ItemCardViewModel> items,
        long revision,
        CancellationToken cancellationToken)
    {
        return _dispatcher.HasThreadAccess
            ? ApplyOnDispatcherAsync()
            : _dispatcher.EnqueueAsync(ApplyOnDispatcherAsync);

        Task ApplyOnDispatcherAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectionCollection.SynchronizeById(
                RecentItems,
                items,
                static item => item.Id,
                static (existing, incoming) =>
                {
                    existing.Update(incoming.Item);
                    existing.Thumbnail = incoming.Thumbnail;
                    existing.DragStorageItem = incoming.DragStorageItem;
                });
            _stateMachine.SetTemporaryItemCount(_mainViewModel.SpaceItemCount);
            OnPropertyChanged(nameof(CompactTitle));
            _logger.LogInformation(
                "Overlay projection applied serialized Temporary Space revision {Revision}, item count {ItemCount}, recent count {RecentCount}.",
                revision,
                _mainViewModel.SpaceItemCount,
                RecentItems.Count);
            return Task.CompletedTask;
        }
    }

    private void OnSpaceProjectionChanged(object? sender, SpaceProjectionChangedEventArgs args)
    {
        _ = ObserveProjectionRefreshAsync(_projectionRefresh.RequestAsync(args.Revision), args.Revision);
    }

    private async Task ObserveProjectionRefreshAsync(Task refresh, long revision)
    {
        try
        {
            await refresh;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Serialized Overlay projection refresh failed for revision {Revision}.", revision);
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnMainViewModelPropertyChanged(sender, args));
            return;
        }

        if (args.PropertyName == nameof(MainViewModel.SpaceItemCount))
        {
            // SpaceProjectionChanged owns data refresh and state-count publication. Keeping the
            // property notification passive prevents the old duplicate Clear/Add reentrancy.
            return;
        }
        else if (args.PropertyName == nameof(MainViewModel.Settings))
        {
            _pendingSettings = null;
            OnPropertyChanged(nameof(MonitorPreference));
            OnPropertyChanged(nameof(MotionPreference));
            OnPropertyChanged(nameof(FileDragWakeMode));
            OnPropertyChanged(nameof(PlacementMode));
            OnPropertyChanged(nameof(QuickPanelHotkey));
            OnPropertyChanged(nameof(SmartDragExcludedProcesses));
        }
    }

    private void OnStateChanged(object? sender, OverlaySnapshot snapshot)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnStateChanged(sender, snapshot));
            return;
        }

        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }
}
