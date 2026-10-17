using DropSpace.App.Services;
using DropSpace.Core.Models;

namespace DropSpace.App.ViewModels;

public sealed partial class MainViewModel
{
    public async Task NavigateAsync(string section, CancellationToken cancellationToken = default)
    {
        section = section is "Clipboard" or "Pinned" or "Settings" or "Music" ? section : "Space";
        var destinationChanged = !string.Equals(section, CurrentSection, StringComparison.Ordinal);
        Interlocked.Increment(ref _reloadRevision);
        _clipboardReloadCaptures = null;
        if (destinationChanged)
        {
            // The previous collection cannot supply rows or a paging boundary for this destination.
            // A same-section refresh keeps its accepted cards and their asset work until replacement.
            _projectionCursor = null;
            HasMoreItems = false;
            SelectedItem = null;
            ClearMainProjection();
            ItemCount = 0;
            // No successful query has confirmed that the new destination is empty yet.
            IsEmpty = false;
            Status = AppUiMessage.Empty;
        }
        CurrentSection = section;
        IsSettingsVisible = string.Equals(section, "Settings", StringComparison.Ordinal);
        switch (section)
        {
            case "Clipboard":
                PageTitle = _strings.Get("PageTitleClipboard");
                PageDescription = _strings.Get("PageDescriptionClipboard");
                break;
            case "Pinned":
                PageTitle = _strings.Get("PageTitlePinned");
                PageDescription = _strings.Get("PageDescriptionPinned");
                break;
            case "Settings":
                PageTitle = _strings.Get("PageTitleSettings");
                PageDescription = _strings.Get("PageDescriptionSettings");
                IsBusy = false;
                ClearMainProjection();
                _projectionCursor = null;
                HasMoreItems = false;
                ItemCount = 0;
                IsEmpty = true;
                return;
            case "Music":
                PageTitle = _strings.Get("PageTitleMusic");
                PageDescription = _strings.Get("PageDescriptionMusic");
                IsBusy = false; ClearMainProjection(); _projectionCursor = null;
                HasMoreItems = false; ItemCount = 0; IsEmpty = true;
                return;
            default:
                CurrentSection = "Space";
                PageTitle = _strings.Get("PageTitleSpace");
                PageDescription = _strings.Get("PageDescriptionSpace");
                break;
        }

        await ReloadProjectionAsync(reportNavigationFailure: destinationChanged, cancellationToken);
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        ReloadProjectionAsync(reportNavigationFailure: false, cancellationToken);

    private async Task ReloadProjectionAsync(bool reportNavigationFailure, CancellationToken cancellationToken)
    {
        if (!IsCollectionVisible) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        var revision = Interlocked.Increment(ref _reloadRevision);
        var request = new ItemProjectionRequest(CurrentSection, SearchText);
        // Captures arriving after the query snapshot must survive replacement of the first page.
        // This journal belongs only to this reload and is bounded by the live projection cap.
        var journal = new ProjectionLoadJournal();
        var captureClipboard = request.Section == "Clipboard" && string.IsNullOrWhiteSpace(request.SearchText);
        _clipboardReloadCaptures = captureClipboard ? journal : null;
        _projectionLoads.Add(journal);
        IsBusy = true;
        try
        {
            var page = await _projection.LoadPageAsync(request, null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || revision != Volatile.Read(ref _reloadRevision) || IsSettingsVisible ||
                !ProjectionRequestStillCurrent(request))
            {
                return;
            }

            IReadOnlyList<DropItem> projectedItems = page.Items.Where(item => !journal.RemovedIds.Contains(item.Id)).ToArray();
            var liveOverflow = journal.CaptureOverflow;
            if (journal.Captures.Count > 0)
            {
                var captures = journal.Captures.Where(item => !journal.RemovedIds.Contains(item.Id)).ToArray();
                var capturedIds = captures.Select(item => item.Id).ToHashSet();
                var merged = captures.Concat(projectedItems.Where(item => !capturedIds.Contains(item.Id))).ToArray();
                liveOverflow |= merged.Length > MaximumLiveClipboardItems;
                projectedItems = merged.Take(MaximumLiveClipboardItems).ToArray();
            }

            ClearMainProjection();
            AppendProjectionItems(projectedItems);
            ApplyBatchProjectionState();
            _projectionCursor = page.NextCursor;
            HasMoreItems = page.HasMore;
            if (liveOverflow)
            {
                // If removal filtered every visible row, the journal still owns the
                // old retained boundary from which evicted persisted rows can be read.
                var tail = Items.LastOrDefault()?.Item ?? journal.Captures.Last();
                _projectionCursor = new ItemQueryCursor(0, tail.CreatedAtUtc, tail.Id);
                HasMoreItems = true;
            }
            ItemCount = Items.Count;
            IsEmpty = Items.Count == 0;

            Status = !string.IsNullOrWhiteSpace(request.SearchText) && Items.Count == 0
                ? AppUiMessage.Resource("SearchNoMatches")
                : AppUiMessage.Empty;
        }
        catch (Exception exception) when (reportNavigationFailure)
        {
            // Late failures belong to the retired request, just like late successful pages.
            if (!_disposed && revision == Volatile.Read(ref _reloadRevision) &&
                ProjectionRequestStillCurrent(request))
            {
                Status = AppUiMessage.Resource(exception is OperationCanceledException
                    ? "ActionCancelled"
                    : "OperationIncompleteContent");
            }
            throw;
        }
        finally
        {
            _projectionLoads.Remove(journal);
            if (ReferenceEquals(_clipboardReloadCaptures, journal)) _clipboardReloadCaptures = null;
            if (revision == Volatile.Read(ref _reloadRevision))
            {
                IsBusy = false;
            }
        }
    }

    public async Task LoadMoreItemsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy || !HasMoreItems || IsSettingsVisible || _projectionCursor is null)
        {
            return;
        }

        if (!await _projectionLoadGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        var journal = new ProjectionLoadJournal();
        _projectionLoads.Add(journal);
        try
        {
            var revision = Volatile.Read(ref _reloadRevision);
            var request = new ItemProjectionRequest(CurrentSection, SearchText);
            var cursor = _projectionCursor;
            if (cursor is null) return;

            var page = await _projection.LoadPageAsync(request, cursor, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || revision != Volatile.Read(ref _reloadRevision) || IsSettingsVisible ||
                !ProjectionRequestStillCurrent(request))
            {
                return;
            }

            AppendProjectionItems(page.Items.Where(item => !journal.RemovedIds.Contains(item.Id)));
            ApplyBatchProjectionState();
            _projectionCursor = page.NextCursor;
            HasMoreItems = page.HasMore;
            ItemCount = Items.Count;
            IsEmpty = Items.Count == 0;
        }
        finally
        {
            _projectionLoads.Remove(journal);
            _projectionLoadGate.Release();
        }
    }

    private bool ProjectionRequestStillCurrent(ItemProjectionRequest request) =>
        string.Equals(request.Section, CurrentSection, StringComparison.Ordinal) &&
        string.Equals(request.SearchText, SearchText, StringComparison.Ordinal);

    private void AppendProjectionItems(IEnumerable<DropItem> items)
    {
        var knownIds = Items.Select(card => card.Id).ToHashSet();
        foreach (var item in items)
        {
            if (!knownIds.Add(item.Id)) continue;
            var card = new ItemCardViewModel(item, _strings);
            RefreshPrimaryQuickActions(card);
            Items.Add(card);
            _ = LoadMainCardAssetsAsync(card);
        }
    }

    private void ClearMainProjection()
    {
        // Retire accepted cards when a replacement page arrives or the destination changes.
        // Failed/stale same-section refreshes retain the visible cards and their work.
        CancelAllMainCardAssets();
        Items.Clear();
    }

    // Each pending page owns only the removals that can invalidate its snapshot.
    // Undo starts a new reload, so its fresh page has no predecessor's tombstones.
    private sealed class ProjectionLoadJournal
    {
        public List<DropItem> Captures { get; } = [];
        public HashSet<Guid> RemovedIds { get; } = [];
        public bool CaptureOverflow { get; set; }
    }
}
