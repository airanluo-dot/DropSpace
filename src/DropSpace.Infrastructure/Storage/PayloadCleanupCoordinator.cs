using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Storage;

/// <summary>Drains the transactional payload-delete outbox and owns physical cleanup.</summary>
public sealed class PayloadCleanupCoordinator(
    AppStoragePaths paths,
    IPayloadCleanupRepository repository,
    IPayloadStore payloads,
    OwnedPayloadReconciler reconciler,
    ILogger<PayloadCleanupCoordinator> logger) : IPayloadCleanupCoordinator
{
    private const int BatchSize = 256;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<int> DrainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await repository.GetPendingPayloadDeletesAsync(BatchSize, cancellationToken).ConfigureAwait(false);
            var completed = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryDeleteAsync(entry, cancellationToken).ConfigureAwait(false))
                {
                    completed++;
                }
            }

            return completed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await repository.GetPendingPayloadDeletesAsync(BatchSize, cancellationToken).ConfigureAwait(false);
            var completed = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await TryDeleteAsync(entry, cancellationToken).ConfigureAwait(false))
                {
                    completed++;
                }
            }

            var reconciled = await reconciler.ReconcileAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return completed + reconciled;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryDeleteAsync(
        PayloadDeleteOutboxEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(paths.Payloads, entry.RelativePath);
            await payloads.DeleteAsync(
                    Path.GetRelativePath(paths.Payloads, path),
                    cancellationToken)
                .ConfigureAwait(false);
            await repository.CompletePayloadDeleteAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            var category = exception is InvalidDataException or ArgumentException
                ? "malformed-relative-path"
                : exception.GetType().Name;
            try
            {
                await repository.RecordPayloadDeleteFailureAsync(entry.Id, category, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception persistenceException) when (persistenceException is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogError(persistenceException, "Payload cleanup failure could not update outbox entry {EntryId}.", entry.Id);
            }

            logger.LogWarning(
                exception,
                "Owned payload cleanup retained outbox entry {EntryId} with category {ErrorCategory}.",
                entry.Id,
                category);
            return false;
        }
    }
}
