using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

namespace DropSpace.App.Services;

/// <summary>
/// Owns the batch-pin decision and delegates one atomic mutation to the repository boundary.
/// UI projections never perform per-row pin writes themselves.
/// </summary>
public sealed class PinItemsUseCase(IItemRepository repository)
{
    public async Task<BatchPinResult> ToggleAsync(
        IReadOnlyList<DropItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return new BatchPinResult(new Dictionary<Guid, bool>(), Array.Empty<Guid>());
        }

        var target = items.Any(item => !item.IsPinned);
        return await repository.SetPinnedManyAsync(
                items.Select(item => item.Id).ToArray(),
                target,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BatchPinResult> SetAsync(
        IReadOnlyCollection<Guid> ids,
        bool isPinned,
        CancellationToken cancellationToken = default) =>
        repository.SetPinnedManyAsync(ids, isPinned, cancellationToken);
}
