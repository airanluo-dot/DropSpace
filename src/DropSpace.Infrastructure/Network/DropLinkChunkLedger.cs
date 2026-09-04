using System.Collections.Concurrent;

namespace DropSpace.Infrastructure.Network;

/// <summary>
/// Owns the atomic chunk-index ledger so concurrent uploads can never double-count a chunk.
/// </summary>
internal sealed class DropLinkChunkLedger
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<int, long>> _chunks = new();
    private long _transferredBytes;

    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

    public bool TryGet(Guid itemId, int index, out long length)
    {
        length = 0;
        return itemId != Guid.Empty &&
            index >= 0 &&
            _chunks.TryGetValue(itemId, out var itemChunks) &&
            itemChunks.TryGetValue(index, out length);
    }

    public bool TryAdd(Guid itemId, int index, long length)
    {
        if (itemId == Guid.Empty || index < 0 || length < 0)
        {
            return false;
        }

        var itemChunks = _chunks.GetOrAdd(itemId, static _ => new ConcurrentDictionary<int, long>());
        if (!itemChunks.TryAdd(index, length))
        {
            return false;
        }

        Interlocked.Add(ref _transferredBytes, length);
        return true;
    }

    public int GetItemCount(Guid itemId) =>
        _chunks.TryGetValue(itemId, out var itemChunks) ? itemChunks.Count : 0;

    public IReadOnlyDictionary<Guid, IReadOnlyList<int>> Snapshot() =>
        _chunks.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.Keys.Order().ToArray());
}
