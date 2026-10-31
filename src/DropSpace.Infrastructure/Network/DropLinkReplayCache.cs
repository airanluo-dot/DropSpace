namespace DropSpace.Infrastructure.Network;

/// <summary>
/// Bounded replay protection for one-time handoff sessions. A TTL alone is not sufficient because
/// a peer can otherwise fill the process heap before the sweep runs.
/// </summary>
public sealed class DropLinkReplayCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryReserve(Guid peerId, Guid sessionId, DateTimeOffset now) =>
        TryReserve(peerId, sessionId, now, now);

    public bool TryReserve(Guid peerId, Guid sessionId, DateTimeOffset now, DateTimeOffset createdAtUtc)
    {
        if (peerId == Guid.Empty || sessionId == Guid.Empty ||
            createdAtUtc < now - DropLinkProtocolPolicy.HandoffReplayRetention ||
            createdAtUtc > now + DropLinkProtocolPolicy.HandoffMaximumFutureSkew)
        {
            return false;
        }

        var key = string.Concat(peerId.ToString("N"), ":", sessionId.ToString("N"));
        var prefix = string.Concat(peerId.ToString("N"), ":");
        lock (_gate)
        {
            RemoveExpired(now);
            if (_entries.ContainsKey(key) ||
                _entries.Count >= DropLinkProtocolPolicy.MaximumHandoffReplayEntries ||
                _entries.Keys.Count(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal)) >=
                    DropLinkProtocolPolicy.MaximumHandoffReplayEntriesPerPeer)
            {
                return false;
            }

            // A sender's permitted future clock skew extends the message's validity.
            // Keep its reservation until that entire validity window has elapsed.
            var retainedFrom = createdAtUtc > now ? createdAtUtc : now;
            _entries.Add(key, retainedFrom + DropLinkProtocolPolicy.HandoffReplayRetention);
            return true;
        }
    }

    public void Remove(Guid peerId, Guid sessionId)
    {
        if (peerId == Guid.Empty || sessionId == Guid.Empty)
        {
            return;
        }

        lock (_gate)
        {
            _entries.Remove(string.Concat(peerId.ToString("N"), ":", sessionId.ToString("N")));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var entry in _entries.Where(entry => now > entry.Value).ToArray())
        {
            _entries.Remove(entry.Key);
        }
    }
}
