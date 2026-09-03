namespace DropSpace.Infrastructure.Network;

/// <summary>Bounded replay protection for already-known DropLink peers.</summary>
public sealed class DropLinkNonceCache
{
    public const int MaximumEntries = 4_096;
    public const int MaximumEntriesPerPeer = 256;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

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

    public bool TryReserve(Guid peerId, string nonce, DateTimeOffset now)
    {
        if (peerId == Guid.Empty || string.IsNullOrWhiteSpace(nonce) || nonce.Length > 256)
        {
            return false;
        }

        var key = string.Concat(peerId.ToString("N"), ":", nonce);
        var prefix = string.Concat(peerId.ToString("N"), ":");
        lock (_gate)
        {
            foreach (var entry in _entries.Where(entry => now - entry.Value > Retention).ToArray())
            {
                _entries.Remove(entry.Key);
            }

            if (_entries.ContainsKey(key) ||
                _entries.Count >= MaximumEntries ||
                _entries.Keys.Count(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal)) >= MaximumEntriesPerPeer)
            {
                return false;
            }

            _entries.Add(key, now);
            return true;
        }
    }

    public void Remove(Guid peerId, string nonce)
    {
        if (peerId == Guid.Empty || string.IsNullOrWhiteSpace(nonce))
        {
            return;
        }

        lock (_gate)
        {
            _entries.Remove(string.Concat(peerId.ToString("N"), ":", nonce));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
