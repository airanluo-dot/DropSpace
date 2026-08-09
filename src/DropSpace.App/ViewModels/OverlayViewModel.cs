using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.App.Services;
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
    private readonly ILogger<OverlayViewModel> _logger;
    private readonly SerializedProjectionRefreshCoordinator<ItemCardViewModel> _projectionRefresh;
    private OverlaySnapshot _snapshot;
    private AppSettings? _pendingSettings;
    private TaskCompletionSource? _modeTransitionCompletion;
    private OverlayDisplayMode? _modeTransitionExpected;
    private string? _activeMonitorId;
    private bool _disposed;

    public OverlayViewModel(
        MainViewModel mainViewModel,
        OverlayStateMachine stateMachine,
        DispatcherQueue dispatcher,
        ILogger<OverlayViewModel> logger)
    {
        _mainViewModel = mainViewModel;
        _stateMachine = stateMachine;
        _dispatcher = dispatcher;
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

    public bool IsDragPromptVisible => Snapshot.State is OverlayState.DragApproaching or OverlayState.DragReady;

    public bool IsCompactVisible => Snapshot.State == OverlayState.Compact;

    public bool IsExpandedVisible => Snapshot.State == OverlayState.Expanded;

    public bool IsExpandedDropTargetActive => Snapshot.ExpandedDropActive;

    public string CompactTitle => Snapshot.TemporaryItemCount switch
    {
        0 => "DropSpace",
        1 when RecentItems.FirstOrDefault() is { } item => item.Title,
        1 => "1 个项目",
        _ => $"{Snapshot.TemporaryItemCount} 个项目",
    };

    public string DragTitle => Snapshot.State == OverlayState.DragReady
        ? "放到 DropSpace"
        : "拖到顶部以暂存";

    public string DragSubtitle => Snapshot.State == OverlayState.DragReady
        ? "松开即可添加文件或文件夹引用"
        : "原始文件不会被移动或删除";

    public async Task InitializeAsync(string initialMonitorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialMonitorId);
        ActiveMonitorId = initialMonitorId;
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        _mainViewModel.SpaceProjectionChanged += OnSpaceProjectionChanged;
        _stateMachine.Changed += OnStateChanged;
        _stateMachine.Restore(_mainViewModel.SpaceItemCount, _mainViewModel.OverlayDisplayMode);
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

    public async Task ExpandAsync(CancellationToken cancellationToken = default)
    {
        await RefreshRecentItemsAsync(cancellationToken);
        _stateMachine.Expand();
    }

    public void Collapse() => _stateMachine.Collapse();

    public void CompleteDismissal() => _stateMachine.CompleteDismissal();

    public void CompleteModeTransition() => _stateMachine.CompleteModeTransition();

    private Task ApplyUiSettingsAsync(AppSettings candidate, CancellationToken cancellationToken)
    {
        candidate.Validate();
        return _dispatcher.HasThreadAccess
            ? ApplyUiSettingsOnDispatcherAsync(candidate, cancellationToken)
            : _dispatcher.EnqueueAsync(() => ApplyUiSettingsOnDispatcherAsync(candidate, cancellationToken));
    }

    private async Task ApplyUiSettingsOnDispatcherAsync(
        AppSettings candidate,
        CancellationToken cancellationToken)
    {
        _pendingSettings = candidate;
        OnPropertyChanged(nameof(MonitorPreference));
        OnPropertyChanged(nameof(MotionPreference));

        var snapshot = _stateMachine.Snapshot;
        if (snapshot.DisplayMode == candidate.OverlayDisplayMode &&
            snapshot.State != OverlayState.ModeTransition)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _modeTransitionCompletion?.TrySetCanceled();
        _modeTransitionCompletion = completion;
        _modeTransitionExpected = candidate.OverlayDisplayMode;
        _stateMachine.RequestDisplayMode(candidate.OverlayDisplayMode);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
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

        _modeTransitionCompletion?.TrySetCanceled();
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
            if (_stateMachine.Snapshot.TargetDisplayMode != _mainViewModel.OverlayDisplayMode)
            {
                _stateMachine.RequestDisplayMode(_mainViewModel.OverlayDisplayMode);
            }
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
        if (_modeTransitionCompletion is { } completion &&
            _modeTransitionExpected is { } expected &&
            snapshot.State != OverlayState.ModeTransition &&
            snapshot.DisplayMode == expected)
        {
            _modeTransitionCompletion = null;
            _modeTransitionExpected = null;
            completion.TrySetResult();
        }

        SnapshotChanged?.Invoke(this, snapshot);
    }
}
