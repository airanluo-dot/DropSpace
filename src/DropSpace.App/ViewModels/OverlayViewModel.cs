using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Audio;
using DropSpace.Core.Collections;
using DropSpace.Core.Models;
using DropSpace.Core.Island;
using DropSpace.Core.Media;
using DropSpace.Core.Overlay;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.ViewModels;

public sealed class OverlayViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly MainViewModel _mainViewModel;
    private readonly OverlayStateMachine _stateMachine;
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<OverlayViewModel> _logger;
    private readonly IIslandActivityRouter _activityRouter;
    private readonly IMediaSessionService _media;
    private readonly IAudioSpectrumService _spectrum;
    private readonly SerializedProjectionRefreshCoordinator<ItemCardViewModel> _projectionRefresh;
    private OverlaySnapshot _snapshot;
    private AppSettings? _pendingSettings;
    private string? _activeMonitorId;
    private string? _shellAcknowledgement;
    private CancellationTokenSource? _shellAcknowledgementCancellation;
    private bool _disposed;
    private IslandActivitySnapshot _activitySnapshot = IslandActivitySnapshot.Empty;
    private IReadOnlyList<float> _spectrumBars = SpectrumFrame.Empty.Bars;
    private Guid _dropActivityId;
    private Guid _manualActivityId;

    public OverlayViewModel(
        MainViewModel mainViewModel,
        OverlayStateMachine stateMachine,
        DispatcherQueue dispatcher,
        IAppStringLocalizer strings,
        ILogger<OverlayViewModel> logger,
        IIslandActivityRouter activityRouter,
        IMediaSessionService media,
        IAudioSpectrumService spectrum)
    {
        _mainViewModel = mainViewModel;
        _stateMachine = stateMachine;
        _dispatcher = dispatcher;
        _strings = strings;
        _logger = logger;
        _activityRouter = activityRouter;
        _media = media;
        _spectrum = spectrum;
        _snapshot = stateMachine.Snapshot;
        _projectionRefresh = new SerializedProjectionRefreshCoordinator<ItemCardViewModel>(
            cancellationToken => _mainViewModel.GetRecentSpaceItemsAsync(5, cancellationToken),
            ApplyRecentItemsAsync);
        _mainViewModel.UiSettingsPreflightAsync = ApplyUiSettingsAsync;
        _activitySnapshot = _activityRouter.Snapshot;
        _activityRouter.Changed += OnActivityChanged;
        _spectrum.FrameChanged += OnSpectrumChanged;
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
                OnPropertyChanged(nameof(IsNativeActivityVisible));
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

    public IslandActivitySnapshot ActivitySnapshot => _activitySnapshot;

    public bool IsNativeActivityVisible => _activitySnapshot.Current is not null &&
        (Snapshot.State is OverlayState.Compact or OverlayState.Expanded) &&
        Snapshot.State is not OverlayState.DragApproaching and not OverlayState.DragReady;

    public string ActivityTitle => _activitySnapshot.Current?.CompactTitle ?? string.Empty;

    public string ActivitySubtitle => _activitySnapshot.Current?.CompactSubtitle ?? string.Empty;

    public string ActivityExpandedTitle => _activitySnapshot.Current?.ExpandedTitle ?? string.Empty;

    public string ActivityExpandedSubtitle => _activitySnapshot.Current?.ExpandedSubtitle ?? string.Empty;

    public bool IsMediaActivity => _activitySnapshot.Current?.Kind == IslandActivityKind.Media;

    public bool IsMediaControlsVisible => IsMediaActivity &&
        (_pendingSettings ?? _mainViewModel.Settings).IslandActivity.ShowCompactControls;

    public IReadOnlyList<float> SpectrumBars => _spectrumBars;

    public bool IsSpectrumVisible => IsMediaActivity &&
        _spectrum.Current.CaptureMode != SpectrumCaptureMode.Unavailable;

    public string CompactTitle => _shellAcknowledgement ?? (Snapshot.TemporaryItemCount switch
    {
        0 => _strings.Get("OverlayTitle"),
        1 when RecentItems.FirstOrDefault() is { } item => item.Title,
        _ => _strings.Format("OverlayItemCount", Snapshot.TemporaryItemCount),
    });

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
        _appliedSettings = _mainViewModel.Settings;
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
        PublishDropActivity("Drop ready", "Release to add to DropSpace");
        _stateMachine.BeginDragApproach();
    }

    public void BeginVisibleDragApproach(string monitorId)
    {
        ActiveMonitorId = monitorId;
        PublishDropActivity("Drop ready", "Release to add to DropSpace");
        _stateMachine.BeginVisibleDrag();
    }

    public void SetActiveMonitor(string monitorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        ActiveMonitorId = monitorId;
    }

    public void SetDragReady(bool ready) => _stateMachine.SetDragReady(ready);

    public void CancelDrag()
    {
        _stateMachine.CancelDrag();
        RemoveDropActivity();
    }

    public async Task<int> CompleteDropAsync(
        string monitorId,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ActiveMonitorId = monitorId;
        var accepted = await _mainViewModel.AddPathsAsync(paths, cancellationToken);
        await RefreshRecentItemsAsync(cancellationToken);
        _stateMachine.CompleteDrop(_mainViewModel.SpaceItemCount);
        RemoveDropActivity();
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
        RemoveDropActivity();
        return accepted;
    }

    public async Task<int> CompleteOwnedDropAsync(
        string monitorId,
        IEnumerable<string> paths,
        StagingLease lease,
        bool visibleTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ActiveMonitorId = monitorId;
        var accepted = await _mainViewModel.AddOwnedPathsBatchAsync(
            paths,
            null,
            "ole-virtual-file-drop",
            2L * 1024 * 1024 * 1024,
            cancellationToken,
            lease);
        await RefreshRecentItemsAsync(cancellationToken);
        if (visibleTarget)
        {
            _stateMachine.CompleteVisibleDrop(_mainViewModel.SpaceItemCount);
        }
        else
        {
            _stateMachine.CompleteDrop(_mainViewModel.SpaceItemCount);
        }
        RemoveDropActivity();
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
        RemoveDropActivity();
    }

    public async Task ExpandAsync(CancellationToken cancellationToken = default)
    {
        await RefreshRecentItemsAsync(cancellationToken);
        if (_manualActivityId == Guid.Empty)
        {
            _manualActivityId = _activityRouter.Publish(new IslandActivity(
                Guid.Empty,
                IslandActivityKind.ManualExpanded,
                IslandActivityPriority.ManualExpanded,
                IslandActivityPresentation.Expanded,
                "manual-expanded",
                "DropSpace",
                "Temporary Space",
                "DropSpace",
                "Temporary Space",
                new(DateTimeOffset.UtcNow)));
        }
        _stateMachine.Expand();
    }

    public void Collapse()
    {
        _stateMachine.Collapse();
        if (_manualActivityId != Guid.Empty)
        {
            _activityRouter.Remove(_manualActivityId);
            _manualActivityId = Guid.Empty;
        }
    }

    public Task PlayPauseMediaAsync(CancellationToken cancellationToken = default) => _media.PlayPauseAsync(cancellationToken);

    public Task SkipNextMediaAsync(CancellationToken cancellationToken = default) => _media.SkipNextAsync(cancellationToken);

    public Task SkipPreviousMediaAsync(CancellationToken cancellationToken = default) => _media.SkipPreviousAsync(cancellationToken);

    public void CompleteDismissal() => _stateMachine.CompleteDismissal();

    private AppSettings? _appliedSettings;

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
        var previous = _pendingSettings ?? _appliedSettings ?? _mainViewModel.Settings;
        _pendingSettings = candidate;
        _appliedSettings = candidate;
        NotifySettingsChanges(previous, candidate);
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

    public Task<ItemActionResult> ExecuteQuickActionAsync(
        QuickActionButtonViewModel quickAction,
        CancellationToken cancellationToken = default) =>
        _mainViewModel.ExecuteQuickActionAsync(quickAction.Card, quickAction.ActionId, cancellationToken);

    public Task<ItemActionResult> ExecuteQuickActionAsync(
        QuickActionButtonViewModel quickAction,
        ItemActionContext context,
        CancellationToken cancellationToken = default) =>
        _mainViewModel.ExecuteQuickActionAsync(quickAction.Card, quickAction.ActionId, context, cancellationToken);

    public async Task ShowShellIntakeAcknowledgementAsync(
        int acceptedCount,
        CancellationToken cancellationToken = default)
    {
        if (acceptedCount <= 0)
        {
            return;
        }

        var message = acceptedCount == 1
            ? _strings.Get("ShellIntakeAdded")
            : _strings.Format("ShellIntakeAddedCount", acceptedCount);
        using var acknowledgementCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task DispatchAsync(Func<Task> action)
        {
            if (_dispatcher.HasThreadAccess)
            {
                await action().ConfigureAwait(false);
            }
            else
            {
                await _dispatcher.EnqueueAsync(action).ConfigureAwait(false);
            }
        }

        await DispatchAsync(() =>
        {
            _shellAcknowledgementCancellation?.Cancel();
            _shellAcknowledgementCancellation = acknowledgementCancellation;
            _shellAcknowledgement = message;
            OnPropertyChanged(nameof(CompactTitle));
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), acknowledgementCancellation.Token).ConfigureAwait(false);
            await DispatchAsync(() =>
            {
                if (ReferenceEquals(_shellAcknowledgementCancellation, acknowledgementCancellation))
                {
                    _shellAcknowledgement = null;
                    _shellAcknowledgementCancellation = null;
                    OnPropertyChanged(nameof(CompactTitle));
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A newer shell invocation replaced this acknowledgement.
        }
    }

    public async Task RefreshRecentItemsAsync(CancellationToken cancellationToken = default)
    {
        await _projectionRefresh.RequestAsync(_mainViewModel.SpaceRevision, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _projectionRefresh.DisposeAsync();
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
        _activityRouter.Changed -= OnActivityChanged;
        _spectrum.FrameChanged -= OnSpectrumChanged;
        RemoveDropActivity();
        if (_manualActivityId != Guid.Empty) _activityRouter.Remove(_manualActivityId);
        if (_mainViewModel.UiSettingsPreflightAsync == ApplyUiSettingsAsync)
        {
            _mainViewModel.UiSettingsPreflightAsync = null;
        }

        _projectionRefresh.Dispose();
        _shellAcknowledgementCancellation?.Cancel();
        _shellAcknowledgementCancellation = null;
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
                (existing, incoming) =>
                {
                    existing.Update(incoming.Item);
                    existing.Thumbnail = incoming.Thumbnail;
                    existing.DragStorageItem = incoming.DragStorageItem;
                    _mainViewModel.RefreshPrimaryQuickActions(existing);
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
            var previous = _pendingSettings ?? _appliedSettings;
            _pendingSettings = null;
            _appliedSettings = _mainViewModel.Settings;
            // The preflight already applied this snapshot. Do not repeat native work
            // for every settings notification (including update-check timestamps).
            if (previous is not null) NotifySettingsChanges(previous, _mainViewModel.Settings);
        }
    }

    private void NotifySettingsChanges(AppSettings previous, AppSettings next)
    {
        if (previous.OverlayMonitor != next.OverlayMonitor) OnPropertyChanged(nameof(MonitorPreference));
        if (previous.OverlayMotion != next.OverlayMotion) OnPropertyChanged(nameof(MotionPreference));
        if (previous.FileDragWakeMode != next.FileDragWakeMode) OnPropertyChanged(nameof(FileDragWakeMode));
        if (previous.OverlayPlacementMode != next.OverlayPlacementMode ||
            !previous.OverlayPlacements.OrderBy(pair => pair.Key).SequenceEqual(next.OverlayPlacements.OrderBy(pair => pair.Key)))
            OnPropertyChanged(nameof(PlacementMode));
        if (previous.QuickPanelHotkey != next.QuickPanelHotkey) OnPropertyChanged(nameof(QuickPanelHotkey));
        if (previous.IslandActivity.ShowCompactControls != next.IslandActivity.ShowCompactControls)
            OnPropertyChanged(nameof(IsMediaControlsVisible));
        if (!previous.SmartDragExcludedProcesses.SequenceEqual(next.SmartDragExcludedProcesses))
            OnPropertyChanged(nameof(SmartDragExcludedProcesses));
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

    private void OnActivityChanged(object? sender, IslandActivitySnapshot snapshot)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnActivityChanged(sender, snapshot));
            return;
        }

        _activitySnapshot = snapshot;
        _stateMachine.SetNativeActivityVisible(snapshot.Current is not null);
        OnPropertyChanged(nameof(ActivitySnapshot));
        OnPropertyChanged(nameof(IsNativeActivityVisible));
        OnPropertyChanged(nameof(IsMediaActivity));
        OnPropertyChanged(nameof(IsMediaControlsVisible));
        OnPropertyChanged(nameof(IsSpectrumVisible));
        OnPropertyChanged(nameof(ActivityTitle));
        OnPropertyChanged(nameof(ActivitySubtitle));
        OnPropertyChanged(nameof(ActivityExpandedTitle));
        OnPropertyChanged(nameof(ActivityExpandedSubtitle));
    }

    private void OnSpectrumChanged(object? sender, SpectrumFrame frame)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnSpectrumChanged(sender, frame));
            return;
        }

        _spectrumBars = frame.Bars;
        OnPropertyChanged(nameof(SpectrumBars));
        OnPropertyChanged(nameof(IsSpectrumVisible));
    }

    private void PublishDropActivity(string title, string subtitle)
    {
        RemoveDropActivity();
        _dropActivityId = _activityRouter.Publish(new IslandActivity(
            Guid.Empty,
            IslandActivityKind.Drop,
            IslandActivityPriority.Drop,
            IslandActivityPresentation.Both,
            "drop",
            title,
            subtitle,
            "DropSpace",
            subtitle,
            new(DateTimeOffset.UtcNow),
            false));
    }

    private void RemoveDropActivity()
    {
        if (_dropActivityId != Guid.Empty)
        {
            _activityRouter.Remove(_dropActivityId);
            _dropActivityId = Guid.Empty;
        }
        _activityRouter.RemoveSource("drop");
    }
}
