using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Collections;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.ViewModels;

public sealed class ClipboardIslandViewModel : ObservableObject, IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly ClipboardCaptureService _capture;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<ClipboardIslandViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<object> _owners = [];
    private readonly Channel<bool> _refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private bool _visible, _disposed;
    private long _visibilityGeneration;
    private AppUiMessage _error = AppUiMessage.Empty;
    private ClipboardErrorOrigin _errorOrigin;
    public ClipboardIslandViewModel(MainViewModel main, ClipboardCaptureService capture, IAppStringLocalizer strings, ILogger<ClipboardIslandViewModel> logger, DispatcherQueue dispatcher)
    {
        _main = main; _capture = capture; _strings = strings; _logger = logger; _dispatcher = dispatcher;
        _main.PropertyChanged += OnMainChanged;
        _capture.ItemCaptured += OnCaptured;
        _capture.ItemImported += OnCaptured;
        _worker = RunAsync();
    }
    public ObservableCollection<ItemCardViewModel> Items { get; } = [];
    public string Error => _error.Render(_strings);
    private AppUiMessage ErrorMessage { set => SetProperty(ref _error, value, nameof(Error)); }
    public bool Empty => Items.Count == 0;
    public string PauseLabel => _strings.Get(_main.IsClipboardPaused ? "ClipboardIslandResume" : "ClipboardIslandPause");
    public void SetVisible(object owner, bool visible)
    {
        if (_disposed || !(visible ? _owners.Add(owner) : _owners.Remove(owner))) return;
        var anyVisible = _owners.Count > 0;
        if (anyVisible != Volatile.Read(ref _visible))
        {
            // Retire reads only when the shared surface fully hides or reopens.
            // Another visible owner can still use the current read and cached cards.
            Interlocked.Increment(ref _visibilityGeneration);
            Volatile.Write(ref _visible, anyVisible);
        }
        if (anyVisible) _refresh.Writer.TryWrite(true);
    }
    public Task CopyAsync(ItemCardViewModel card) => ExecuteAsync(() => _main.CopyAsync(card), card);
    public async Task PinAsync(ItemCardViewModel card)
    {
        if (!card.IsPinActionEnabled) return;
        card.IsPinActionEnabled = false;
        try { await ExecuteAsync(() => _main.TogglePinAsync(card), card); }
        finally
        {
            // A ToggleButton changes its target before the async write completes.
            // Reapply the last known card state without replacing its UI binding;
            // a failed follow-up read does not prove the write was rolled back.
            card.RefreshPinState();
            card.IsPinActionEnabled = true;
        }
    }
    public Task RemoveAsync(ItemCardViewModel card) => ExecuteAsync(() => _main.RemoveAsync(card), card);
    public Task TogglePauseAsync() => ExecuteAsync(() => _main.SetClipboardPausedAsync(!_main.IsClipboardPaused));
    internal long ReserveMainNavigation() => _main.ReserveNavigation();
    internal bool IsMainNavigationCurrent(long revision) => !_disposed && _main.IsNavigationCurrent(revision);
    public Task OpenMainAsync() => OpenMainAsync(ReserveMainNavigation());
    internal Task OpenMainAsync(long navigationRevision) => IsMainNavigationCurrent(navigationRevision)
        ? _main.NavigateAsync("Clipboard", navigationRevision)
        : Task.CompletedTask;
    private async Task ExecuteAsync(Func<Task> action, ItemCardViewModel? card = null)
    {
        var visibilityGeneration = Volatile.Read(ref _visibilityGeneration);
        var presentationGeneration = card?.PresentationGeneration ?? 0;
        try
        {
            SetError(AppUiMessage.Empty, ClipboardErrorOrigin.None);
            await action();
            _refresh.Writer.TryWrite(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Clipboard island action failed ({Category}).", exception.GetType().Name);
            // Presentation retirement does not cancel the operation, but its late
            // failure no longer owns feedback. Pin/last-used metadata can advance independently.
            if (!_disposed && visibilityGeneration == Volatile.Read(ref _visibilityGeneration) &&
                (card is null || card.IsPresentationCurrent(presentationGeneration)))
                SetError(AppUiMessage.Resource("ClipboardIslandActionFailed"), ClipboardErrorOrigin.Action);
        }
    }
    private void SetError(AppUiMessage message, ClipboardErrorOrigin origin)
    {
        _errorOrigin = origin;
        ErrorMessage = message;
    }
    private bool IsRefreshCurrent(long visibilityGeneration) => !_disposed && Volatile.Read(ref _visible) &&
        visibilityGeneration == Volatile.Read(ref _visibilityGeneration);
    private void ApplyRecentItems(IReadOnlyList<ItemCardViewModel> incoming)
    {
        var existingById = Items.ToDictionary(static card => card.Id);
        // A removal can retire a visible card while an older read is still queued.
        // Keep that revision check until this snapshot is filtered; a newer Undo
        // revision can restore the item through the normal card merge contract.
        var accepted = incoming.Where(card => !card.IsPresentationRetired &&
            (!existingById.TryGetValue(card.Id, out var existing) || !existing.IsPresentationRetired ||
                card.Item.Revision > existing.Item.Revision)).ToArray();
        var acceptedIds = accepted.Select(static card => card.Id).ToHashSet();
        foreach (var card in Items)
            if (!acceptedIds.Contains(card.Id)) card.RetirePresentation();

        ProjectionCollection.SynchronizeById(Items, accepted, static card => card.Id, (existing, refreshed) =>
        {
            if (existing.TryMergePresentation(refreshed)) _main.RefreshPrimaryQuickActions(existing);
        });
    }
    private void OnCaptured(object? sender, DropItem item) { if (Volatile.Read(ref _visible)) _refresh.Writer.TryWrite(true); }
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Settings)) OnPropertyChanged(nameof(PauseLabel));
        if (args.PropertyName == nameof(MainViewModel.Language))
        {
            foreach (var item in Items) item.RefreshLanguage();
            OnPropertyChanged(nameof(Error));
        }
        if (_visible && args.PropertyName is nameof(MainViewModel.StatusMessage) or nameof(MainViewModel.Settings)) _refresh.Writer.TryWrite(true);
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var _ in _refresh.Reader.ReadAllAsync(_stop.Token))
            {
                if (!Volatile.Read(ref _visible)) continue;
                var visibilityGeneration = Volatile.Read(ref _visibilityGeneration);
                try
                {
                    var items = await _main.GetRecentSourceItemsAsync(ItemSource.Clipboard, 20, _stop.Token);
                    await _dispatcher.EnqueueAsync(() =>
                    {
                        if (IsRefreshCurrent(visibilityGeneration))
                        {
                            ApplyRecentItems(items);
                            // Reading the list successfully does not retry a failed user action.
                            if (_errorOrigin == ClipboardErrorOrigin.Refresh)
                                SetError(AppUiMessage.Empty, ClipboardErrorOrigin.None);
                            OnPropertyChanged(nameof(Empty));
                        }
                        return Task.CompletedTask;
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning("Clipboard island refresh failed ({Category}).", exception.GetType().Name);
                    await _dispatcher.EnqueueAsync(() =>
                    {
                        if (IsRefreshCurrent(visibilityGeneration) && _errorOrigin != ClipboardErrorOrigin.Action)
                            SetError(AppUiMessage.Resource("ClipboardIslandActionFailed"), ClipboardErrorOrigin.Refresh);
                        return Task.CompletedTask;
                    });
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= OnMainChanged;
        _capture.ItemCaptured -= OnCaptured;
        _capture.ItemImported -= OnCaptured;
        _stop.Cancel(); _refresh.Writer.TryComplete(); await _worker; _stop.Dispose();
    }
    private enum ClipboardErrorOrigin { None, Refresh, Action }
}
