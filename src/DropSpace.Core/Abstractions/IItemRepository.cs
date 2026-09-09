using DropSpace.Core.Models;

namespace DropSpace.Core.Abstractions;

public interface IItemRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<DropItem> AddFileAsync(FileCandidate candidate, CancellationToken cancellationToken = default);

    Task<DropItem> AddSpaceFileAsync(
        FileCandidate candidate,
        string? metadataJson,
        CancellationToken cancellationToken = default);

    Task<DropItem> AddOwnedSpaceFileAsync(
        FileCandidate candidate,
        PayloadRecord payload,
        string? metadataJson,
        CancellationToken cancellationToken = default);

    Task<DropItem> AddClipboardFileAsync(
        FileCandidate candidate,
        string fingerprint,
        string? metadataJson,
        CancellationToken cancellationToken = default);

    Task<DropItem> AddTextAsync(TextCandidate candidate, CancellationToken cancellationToken = default);

    Task<DropItem> AddSpaceTextAsync(
        TextCandidate candidate,
        string? metadataJson = null,
        CancellationToken cancellationToken = default);

    Task<DropItem> AddImageAsync(ImageCandidate candidate, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DropItem>> QueryAsync(ItemQuery query, CancellationToken cancellationToken = default);

    async Task<ItemQueryPage> QueryPageAsync(
        ItemQuery query,
        ItemQueryCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (cursor is not null)
        {
            throw new NotSupportedException("This repository does not implement keyset continuation.");
        }

        var items = await QueryAsync(query with { Offset = 0 }, cancellationToken).ConfigureAwait(false);
        return new ItemQueryPage(items, null, false);
    }

    Task<IReadOnlyList<DropItem>> QueryDropBatchAsync(
        Guid dropBatchId,
        CancellationToken cancellationToken = default);

    Task<DropItem?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default);

    Task<BatchPinResult> SetPinnedManyAsync(
        IReadOnlyCollection<Guid> ids,
        bool isPinned,
        CancellationToken cancellationToken = default);

    Task<int> RestorePinnedStatesAsync(
        IReadOnlyDictionary<Guid, bool> previousStates,
        CancellationToken cancellationToken = default);

    Task MarkUsedAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateFileStatusAsync(
        Guid id,
        ItemStatus status,
        string? reason,
        CancellationToken cancellationToken = default);

    Task ReplaceFileReferenceAsync(
        Guid id,
        FileCandidate replacement,
        CancellationToken cancellationToken = default);

    Task<string?> RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> BeginPendingRemovalAsync(
        IReadOnlyCollection<Guid> ids,
        string token,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default);

    Task<int> BeginPendingClipboardClearAsync(
        DateTimeOffset? fromUtc,
        bool includePinned,
        string token,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default);

    Task<int> UndoPendingRemovalAsync(
        string token,
        CancellationToken cancellationToken = default);

    Task<FinalizedRemovalResult> FinalizePendingRemovalAsync(
        string token,
        CancellationToken cancellationToken = default);

    Task<FinalizedRemovalResult> FinalizeExpiredPendingRemovalsAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<ClearResult> ClearClipboardAsync(
        DateTimeOffset? fromUtc,
        bool includePinned,
        CancellationToken cancellationToken = default);

    Task<RetentionResult> ApplyRetentionAsync(
        DateTimeOffset ageCutoffUtc,
        int countLimit,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(ItemSource? source = null, bool pinnedOnly = false, CancellationToken cancellationToken = default);
}
