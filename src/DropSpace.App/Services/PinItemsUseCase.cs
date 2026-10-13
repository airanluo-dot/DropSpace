using DropSpace.Core.Models;

namespace DropSpace.App.Services;

/// <summary>
/// Owns the batch-pin decision and delegates persistence plus Undo ownership to
/// the shared mutation coordinator. UI projections never perform per-row writes.
/// </summary>
public sealed class PinItemsUseCase(UndoCoordinator undo)
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
        return await SetAsync(
                items.Select(item => item.Id).ToArray(),
                target,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BatchPinResult> SetAsync(
        IReadOnlyCollection<Guid> ids,
        bool isPinned,
        CancellationToken cancellationToken = default) =>
        undo.SetPinnedAsync(ids, isPinned, isPinned ? "UndoPinned" : "UndoUnpinned", cancellationToken);
}
