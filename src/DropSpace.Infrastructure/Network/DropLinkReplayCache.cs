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

    public bool TryReserve(Guid peerId, Guid sessionId, DateTimeOffset now)
    {
        if (peerId == Guid.Empty || sessionId == Guid.Empty)
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

            _entries.Add(key, now);
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
        foreach (var entry in _entries.Where(entry =>
                     now - entry.Value > DropLinkProtocolPolicy.HandoffReplayRetention).ToArray())
        {
            _entries.Remove(entry.Key);
        }
    }
}
