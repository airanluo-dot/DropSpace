namespace DropSpace.Core.Transfer;

/// <summary>Bounded, time-limited duplicate guard used by the cross-device clipboard bridge.</summary>
public sealed class ClipboardLoopGuard
{
    public const int DefaultMaximumEntries = 10_000;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset SeenAtUtc)> _order = new();
    private readonly int _maximumEntries;
    private readonly TimeSpan _lifetime;

    public ClipboardLoopGuard(int maximumEntries = DefaultMaximumEntries, TimeSpan? lifetime = null)
    {
        if (maximumEntries is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries;
        _lifetime = lifetime ?? DefaultLifetime;
        if (_lifetime <= TimeSpan.Zero || _lifetime > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(lifetime));
    }

    public bool TryAccept(ClipboardEnvelope envelope, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var key = string.Concat(envelope.Sha256.ToLowerInvariant(), ":", envelope.ByteLength);
        lock (_gate)
        {
            Prune(now);
            if (_entries.TryGetValue(key, out var seenAt) && now - seenAt <= _lifetime) return false;
            _entries[key] = now;
            _order.Enqueue((key, now));
            while (_entries.Count > _maximumEntries && _order.TryDequeue(out var oldest))
            {
                if (_entries.TryGetValue(oldest.Key, out var current) && current == oldest.SeenAtUtc) _entries.Remove(oldest.Key);
            }
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_order.TryPeek(out var oldest) && now - oldest.SeenAtUtc > _lifetime)
        {
            _order.Dequeue();
            if (_entries.TryGetValue(oldest.Key, out var current) && current == oldest.SeenAtUtc) _entries.Remove(oldest.Key);
        }
    }
}
