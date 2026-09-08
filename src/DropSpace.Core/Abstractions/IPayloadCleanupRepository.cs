using DropSpace.Core.Models;

namespace DropSpace.Core.Abstractions;

/// <summary>
/// Durable ownership boundary for app-owned payload cleanup.  Destructive item
/// mutations enqueue obligations in the same SQLite transaction; physical file
/// deletion is deliberately handled by a separate coordinator.
/// </summary>
public interface IPayloadCleanupRepository
{
    Task<IReadOnlyList<PayloadDeleteOutboxEntry>> GetPendingPayloadDeletesAsync(
        int maximumEntries = 256,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetOwnedPayloadPathsAsync(
        CancellationToken cancellationToken = default);

    Task CompletePayloadDeleteAsync(
        string entryId,
        CancellationToken cancellationToken = default);

    Task RecordPayloadDeleteFailureAsync(
        string entryId,
        string errorCategory,
        CancellationToken cancellationToken = default);
}
