using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private OverlaySnapshot _snapshot;
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
            }
        }
    }

    public string? ActiveMonitorId
    {
        get => _activeMonitorId;
        private set => SetProperty(ref _activeMonitorId, value);
    }

    public OverlayMonitorPreference MonitorPreference => _mainViewModel.OverlayMonitor;

    public OverlayMotionPreference MotionPreference => _mainViewModel.OverlayMotion;

    public bool IsDragPromptVisible => Snapshot.State is OverlayState.DragApproaching or OverlayState.DragReady;

    public bool IsCompactVisible => Snapshot.State == OverlayState.Compact;

    public bool IsExpandedVisible => Snapshot.State == OverlayState.Expanded;

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
        _stateMachine.Changed += OnStateChanged;
        _stateMachine.Restore(_mainViewModel.SpaceItemCount, _mainViewModel.OverlayDisplayMode);
        await RefreshRecentItemsAsync(cancellationToken);
    }

    public void BeginDragApproach(string monitorId)
    {
        ActiveMonitorId = monitorId;
        _stateMachine.BeginDragApproach();
    }

    public void SetActiveMonitor(string monitorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        ActiveMonitorId = monitorId;
    }

    public void SetDragReady(bool ready) => _stateMachine.SetDragReady(ready);

    public void CancelDrag() => _stateMachine.CancelDrag();

    public async Task CompleteDropAsync(
        string monitorId,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ActiveMonitorId = monitorId;
        await _mainViewModel.AddPathsAsync(paths, cancellationToken);
        await RefreshRecentItemsAsync(cancellationToken);
        _stateMachine.CompleteDrop(_mainViewModel.SpaceItemCount);
    }

    public async Task ExpandAsync(CancellationToken cancellationToken = default)
    {
        await RefreshRecentItemsAsync(cancellationToken);
        _stateMachine.Expand();
    }

    public void Collapse() => _stateMachine.Collapse();

    public void CompleteDismissal() => _stateMachine.CompleteDismissal();

    public void CompleteModeTransition() => _stateMachine.CompleteModeTransition();

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
        try
        {
            var items = await _mainViewModel.GetRecentSpaceItemsAsync(5, cancellationToken);
            RecentItems.Clear();
            foreach (var item in items)
            {
                RecentItems.Add(item);
            }

            OnPropertyChanged(nameof(CompactTitle));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Overlay recent-items refresh failed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        _stateMachine.Changed -= OnStateChanged;
        _disposed = true;
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
            _stateMachine.SetTemporaryItemCount(_mainViewModel.SpaceItemCount);
            _ = RefreshRecentItemsAsync();
        }
        else if (args.PropertyName == nameof(MainViewModel.Settings))
        {
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
        SnapshotChanged?.Invoke(this, snapshot);
    }
}
