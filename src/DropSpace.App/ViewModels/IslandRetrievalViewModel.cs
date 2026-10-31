using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.ViewModels;

/// <summary>A query projection of existing content; it never owns island activation.</summary>
public sealed class IslandRetrievalViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly IRecentItemsService _recent;
    private readonly ClipboardCaptureService _clipboard;
    private readonly QuickPreviewService _previews;
    private readonly ReferenceWindowService _reference;
    private readonly IAppStringLocalizer _strings;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<IslandRetrievalViewModel> _logger;
    private readonly HashSet<object> _owners = [];
    private CancellationTokenSource? _read;
    private bool _disposed, _pending, _resetPending;
    private int _interactions;
    private long _revision;
    private string _query = string.Empty;
    private int _filter;
    private ItemCardViewModel? _selected;
    private AppUiMessage _status = AppUiMessage.Empty;
    private bool _statusFromRefresh;

    public IslandRetrievalViewModel(MainViewModel main, IRecentItemsService recent,
        ClipboardCaptureService clipboard, QuickPreviewService previews, ReferenceWindowService reference,
        IAppStringLocalizer strings, DispatcherQueue dispatcher, ILogger<IslandRetrievalViewModel> logger)
    {
        _main = main; _recent = recent; _clipboard = clipboard; _previews = previews;
        _reference = reference; _strings = strings; _dispatcher = dispatcher; _logger = logger;
        _recent.Changed += OnRecentChanged;
        _clipboard.ItemCaptured += OnCaptured;
        _clipboard.ItemImported += OnCaptured;
        _main.SpaceProjectionChanged += OnSpaceChanged;
        _main.PropertyChanged += OnMainChanged;
    }

    public ObservableCollection<ItemCardViewModel> Items { get; } = [];
    public string Status => _status.Render(_strings);
    public bool Empty => Items.Count == 0;
    public bool HasSelection => SelectedItem is not null;
    public bool CanReference => SelectedItem is { } selected && ReferenceWindowService.CanOpen(selected.Item);
    public bool CanKeep => SelectedItem?.Item.Source == ItemSource.Recent;
    public bool CanCopyPath => SelectedItem?.Item.File is not null;
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value)) RequestRefresh(reset: true); }
    }
    public int Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) RequestRefresh(reset: true); }
    }
    public ItemCardViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanReference)); OnPropertyChanged(nameof(CanKeep)); OnPropertyChanged(nameof(CanCopyPath));
        }
    }

    public void SetVisible(object owner, bool visible)
    {
        if (_disposed) return;
        if (visible) _owners.Add(owner); else _owners.Remove(owner);
        if (_owners.Count > 0 && visible) RequestRefresh();
        if (_owners.Count == 0) { ++_revision; _read?.Cancel(); }
    }

    // No collection edits or card retirement during native drag or IME composition.
    public void BeginInteraction() { _interactions++; ++_revision; _read?.Cancel(); _pending = true; }
    public void EndInteraction()
    {
        _interactions = Math.Max(0, _interactions - 1);
        if (_interactions == 0 && _pending) RequestRefresh(_resetPending);
    }

    public void RequestRefresh(bool reset = false)
    {
        if (!_dispatcher.HasThreadAccess) { _dispatcher.TryEnqueue(() => RequestRefresh(reset)); return; }
        if (_disposed) return;
        _pending = true; _resetPending |= reset;
        if (_owners.Count == 0 || _interactions > 0) return;
        _read?.Cancel(); _read?.Dispose();
        _read = new CancellationTokenSource();
        var replaceOrder = _resetPending;
        _resetPending = false; _pending = false;
        _ = RefreshAsync(++_revision, replaceOrder, _read.Token);
    }

    private async Task RefreshAsync(long revision, bool reset, CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            var items = (await _main.QueryRetrievalItemsAsync(Query,
                Filter == 1 ? ItemSource.Recent : null, Filter == 2, token)).ToList();
            var selectedReadId = SelectedItem?.Id;
            // A passive arrival can push the selected row beyond the bounded
            // query's newest 100. Confirm its identity separately and retain it.
            if (!reset && SelectedItem is { } selected && !items.Any(card => card.Id == selected.Id) &&
                await _main.ReadRetrievalItemAsync(selected.Id, token) is { } retained &&
                (Filter != 1 || retained.Item.Source == ItemSource.Recent) && (Filter != 2 || retained.IsPinned))
                items.Add(retained);
            if (_disposed || token.IsCancellationRequested || revision != _revision || _interactions > 0 || _owners.Count == 0) return;
            if (!reset && SelectedItem is { } latestSelected && !items.Any(card => card.Id == latestSelected.Id) &&
                Items.Contains(latestSelected) && latestSelected.Id != selectedReadId)
            {
                // Selection moved while its overflow identity was being checked.
                // Retry the passive read instead of publishing a different choice.
                RequestRefresh(); return;
            }
            var selectedId = SelectedItem?.Id;
            var byId = items.ToDictionary(card => card.Id);
            // Preserve live rows and their order on passive arrivals. Search/filter
            // changes are the only operations allowed to reorder the user's results.
            for (var index = Items.Count - 1; index >= 0; index--)
            {
                var existing = Items[index];
                if (byId.TryGetValue(existing.Id, out var incoming)) existing.TryMergePresentation(incoming);
                else { existing.RetirePresentation(); Items.RemoveAt(index); }
            }
            foreach (var incoming in items)
                if (!Items.Any(card => card.Id == incoming.Id)) Items.Add(incoming);
            if (reset)
                for (var index = 0; index < items.Count; index++)
                {
                    var current = Items.IndexOf(Items.First(card => card.Id == items[index].Id));
                    if (current != index) Items.Move(current, index);
                }
            // A passive refresh must also preserve the deliberate absence of a selection.
            // Arrow navigation or an explicit click chooses a result; arrival never does.
            SelectedItem = Items.FirstOrDefault(card => card.Id == selectedId);
            // Only this query's previous failure is repaired by a successful read.
            // Passive arrivals must not erase copy/validation/action feedback.
            if (_statusFromRefresh)
            {
                _statusFromRefresh = false;
                _status = AppUiMessage.Empty;
                OnPropertyChanged(nameof(Status));
            }
            OnPropertyChanged(nameof(Empty)); OnPropertyChanged(nameof(CanKeep)); OnPropertyChanged(nameof(CanReference));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Island retrieval query failed.");
            if (revision == _revision) SetStatus("SearchUnavailable", fromRefresh: true);
        }
    }

    public async Task<bool> CopyAsync(bool pathOnly = false)
    {
        if (SelectedItem is not { } card) return false;
        try
        {
            var item = await ValidateAsync(card);
            if (item is null) return false;
            if (pathOnly && item.File is { } file) await _clipboard.CopyTextAsync(file.OriginalPath);
            else await _main.CopyAsync(card);
            SetStatus("ItemCopied"); return true;
        }
        catch (Exception exception) { ReportFailure(exception); return false; }
    }

    public async Task<PreviewDescriptor?> PreviewAsync()
    {
        if (SelectedItem is not { } card) return null;
        try
        {
            var item = await ValidateAsync(card);
            return item is null ? null : await _previews.LoadAsync(item, inline: false);
        }
        catch (Exception exception) { ReportFailure(exception); return null; }
    }

    public async Task OpenReferenceAsync()
    {
        if (SelectedItem is not { } card) return;
        try { if (await ValidateAsync(card) is { } item) await _reference.OpenAsync(item); }
        catch (Exception exception) { ReportFailure(exception); }
    }

    public async Task KeepAsync()
    {
        if (SelectedItem is not { } card || card.Item.Source != ItemSource.Recent) return;
        try { await _recent.RetainAsync(card.Id); await _main.RefreshRetainedRetrievalAsync(); RequestRefresh(); }
        catch (Exception exception) { ReportFailure(exception); }
    }

    public async Task RemoveAsync()
    {
        if (SelectedItem is not { } card) return;
        try
        {
            if (card.Item.Source == ItemSource.Recent) await _recent.RemoveAsync(card.Id);
            else await _main.RemoveAsync(card);
            RequestRefresh();
        }
        catch (Exception exception) { ReportFailure(exception); }
    }

    private async Task<DropItem?> ValidateAsync(ItemCardViewModel card)
    {
        if (card.Item.Status == ItemStatus.Processing)
        { SetStatus("RetrievalUnavailable"); return null; }
        if (card.Item.Source == ItemSource.Recent || RecentItemMetadata.Read(card.Item.MetadataJson) is not null)
        {
            await _recent.RefreshAsync(card.Id);
            if (await _main.ReadRetrievalItemAsync(card.Id, CancellationToken.None) is not { } refreshed)
            { SetStatus("RetrievalUnavailable"); RequestRefresh(); return null; }
            card.TryMergePresentation(refreshed);
        }
        var item = await _main.ValidateRetrievalItemAsync(card, CancellationToken.None);
        if (item is null || item.Status != ItemStatus.Available)
        { SetStatus("RetrievalUnavailable"); RequestRefresh(); return null; }
        return item;
    }

    private void ReportFailure(Exception exception)
    { _logger.LogWarning(exception, "Island retrieval action failed."); SetStatus("ClipboardIslandActionFailed"); }
    private void SetStatus(string key, bool fromRefresh = false)
    { _statusFromRefresh = fromRefresh; _status = AppUiMessage.Resource(key); OnPropertyChanged(nameof(Status)); }
    private void OnRecentChanged(object? sender, EventArgs args) => RequestRefresh();
    private void OnCaptured(object? sender, DropItem item) => RequestRefresh();
    private void OnSpaceChanged(object? sender, SpaceProjectionChangedEventArgs args) => RequestRefresh();
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.Language) or nameof(MainViewModel.Settings))
            _dispatcher.TryEnqueue(() => { foreach (var item in Items) item.RefreshLanguage(); OnPropertyChanged(string.Empty); });
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_revision; _read?.Cancel(); _read?.Dispose();
        _recent.Changed -= OnRecentChanged; _clipboard.ItemCaptured -= OnCaptured;
        _clipboard.ItemImported -= OnCaptured; _main.SpaceProjectionChanged -= OnSpaceChanged;
        _main.PropertyChanged -= OnMainChanged; _owners.Clear();
    }
}
