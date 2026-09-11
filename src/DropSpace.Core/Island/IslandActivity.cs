namespace DropSpace.Core.Island;

public enum IslandActivityKind
{
    Drop,
    ManualExpanded,
    Notification,
    Volume,
    Media,
    WidgetIdle,
}

public enum IslandActivityPriority
{
    Drop = 0,
    ManualExpanded = 10,
    Notification = 20,
    Volume = 30,
    Media = 40,
    WidgetIdle = 50,
}

public enum IslandActivityPresentation
{
    Compact,
    Expanded,
    Both,
}

public sealed record IslandActivityLifetime(
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && expiresAt <= now;
}

public sealed record IslandActivity(
    Guid Id,
    IslandActivityKind Kind,
    IslandActivityPriority Priority,
    IslandActivityPresentation PreferredPresentation,
    string SourceId,
    string CompactTitle,
    string CompactSubtitle,
    string ExpandedTitle,
    string ExpandedSubtitle,
    IslandActivityLifetime Lifetime,
    bool IsInterruptible = true,
    long Revision = 0)
{
    public bool IsExpired(DateTimeOffset now) => Lifetime.IsExpired(now);
}

public sealed record IslandActivitySnapshot(
    IslandActivity? Current,
    IReadOnlyList<IslandActivity> Activities,
    long Revision)
{
    public static IslandActivitySnapshot Empty { get; } = new(null, [], 0);
}

public interface IIslandActivityRouter
{
    IslandActivitySnapshot Snapshot { get; }

    event EventHandler<IslandActivitySnapshot>? Changed;

    Guid Publish(IslandActivity activity);

    bool Remove(Guid activityId);

    int RemoveSource(string sourceId);

    void Clear();
}

public sealed class IslandActivityRouter : IIslandActivityRouter, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, IslandActivity> _activities = [];
    private long _revision;
    private bool _disposed;

    public event EventHandler<IslandActivitySnapshot>? Changed;

    public IslandActivitySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot(DateTimeOffset.UtcNow);
            }
        }
    }

    public Guid Publish(IslandActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var id = activity.Id == Guid.Empty ? Guid.NewGuid() : activity.Id;
        IslandActivitySnapshot snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activities[id] = activity with
            {
                Id = id,
                Revision = ++_revision,
            };
            snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        }

        Changed?.Invoke(this, snapshot);
        return id;
    }

    public bool Remove(Guid activityId)
    {
        IslandActivitySnapshot? snapshot = null;
        lock (_gate)
        {
            if (_activities.Remove(activityId))
            {
                _revision++;
                snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
            }
        }

        if (snapshot is not null)
        {
            Changed?.Invoke(this, snapshot);
            return true;
        }

        return false;
    }

    public int RemoveSource(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        IslandActivitySnapshot? snapshot = null;
        int removed;
        lock (_gate)
        {
            removed = _activities.Values
                .Where(activity => string.Equals(activity.SourceId, sourceId, StringComparison.Ordinal))
                .Select(activity => activity.Id)
                .ToArray()
                .Count(id => _activities.Remove(id));
            if (removed > 0)
            {
                _revision++;
                snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
            }
        }

        if (snapshot is not null)
        {
            Changed?.Invoke(this, snapshot);
        }

        return removed;
    }

    public void Clear()
    {
        IslandActivitySnapshot? snapshot = null;
        lock (_gate)
        {
            if (_activities.Count > 0)
            {
                _activities.Clear();
                _revision++;
                snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
            }
        }

        if (snapshot is not null)
        {
            Changed?.Invoke(this, snapshot);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _activities.Clear();
            Changed = null;
        }
    }

    private IslandActivitySnapshot CreateSnapshot(DateTimeOffset now)
    {
        foreach (var expired in _activities.Values.Where(activity => activity.IsExpired(now)).Select(activity => activity.Id).ToArray())
        {
            _activities.Remove(expired);
        }

        var activities = _activities.Values
            .OrderBy(activity => activity.Priority)
            .ThenByDescending(activity => activity.Lifetime.CreatedAt)
            .ToArray();
        return new IslandActivitySnapshot(
            activities.FirstOrDefault(),
            activities,
            _revision);
    }
}
