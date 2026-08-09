using DropSpace.Core.Models;

namespace DropSpace.Core.Abstractions;

public interface IItemRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<DropItem> AddFileAsync(FileCandidate candidate, CancellationToken cancellationToken = default);

    Task<DropItem> AddClipboardFileAsync(
        FileCandidate candidate,
        string fingerprint,
        string? metadataJson,
        CancellationToken cancellationToken = default);

    Task<DropItem> AddTextAsync(TextCandidate candidate, CancellationToken cancellationToken = default);

    Task<DropItem> AddImageAsync(ImageCandidate candidate, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DropItem>> QueryAsync(ItemQuery query, CancellationToken cancellationToken = default);

    Task<DropItem?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default);

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
