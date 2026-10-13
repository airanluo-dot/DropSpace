using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;

namespace DropSpace.App.Services;

// Only the Windows clipboard boundary is replaced. The receiver under test calls
// this method itself; successful imports use the real temporary SQLite repository.
public sealed class ClipboardCaptureService(SqliteItemRepository repository)
{
    private EventHandler<DropItem>? _captured;
    public event EventHandler<DropItem>? ItemCaptured
    {
        add => _captured += value;
        remove => _captured -= value;
    }

    public int CaptureHandlerCount => _captured?.GetInvocationList().Length ?? 0;
    public bool IsPaused { get; set; }
    public bool FailNextImport { get; set; }
    public int ImportAttempts { get; private set; }
    public int SuccessfulImports { get; private set; }

    public async Task<DropItem> ImportRemoteAsync(ClipboardEnvelope envelope,
        CancellationToken cancellationToken = default, bool publishCaptured = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (publishCaptured) throw new InvalidOperationException("Remote import must not republish a local capture event.");
        ImportAttempts++;
        if (FailNextImport)
        {
            FailNextImport = false;
            throw new IOException("Injected precommit import failure.");
        }
        var item = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate(envelope.Text!), cancellationToken);
        SuccessfulImports++;
        return item;
    }
}

// This scenario selects no peer channels. Calling discovery is a probe failure,
// so unexpected network activity cannot silently become part of the evidence.
public sealed class DeviceHandoffService
{
    public Task<IReadOnlyList<(PeerDevice Peer, Uri Endpoint)>> DiscoverTrustedPeersAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Peer discovery must not run in this local receiver probe.");
}
