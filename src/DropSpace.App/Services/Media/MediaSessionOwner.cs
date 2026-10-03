namespace DropSpace.App.Services.Media;

/// <summary>Prepares a candidate without changing the canonical session until its read succeeds.</summary>
internal sealed class MediaSessionOwner<T> where T : class
{
    public T? Session { get; private set; }
    public string Identity { get; private set; } = string.Empty;
    public MediaEventSubscription? Subscription { get; private set; }

    // The caller serializes preparation, commit and Clear with its session gate.
    public async Task<TValue> PrepareAndCommitAsync<TValue>(T candidate,
        Func<CancellationToken, Task<MediaEventSubscription?>> subscribe,
        Func<CancellationToken, Task<TValue>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var subscription = ReferenceEquals(candidate, Session) ? Subscription : null;
        var prepared = false;
        try
        {
            if (subscription is null)
            {
                subscription = await subscribe(token);
                prepared = true;
            }
            var value = await read(token);
            token.ThrowIfCancellationRequested();
            var previous = Subscription;
            if (!ReferenceEquals(candidate, Session))
            {
                Session = candidate;
                Identity = Guid.NewGuid().ToString("N");
            }
            Subscription = subscription;
            prepared = false;
            if (!ReferenceEquals(previous, subscription)) previous?.Retire();
            return value;
        }
        finally
        {
            // Failed/canceled preparation never retires the healthy canonical lease.
            // Native attach/remove completion still belongs to the prepared lease.
            if (prepared) subscription?.Retire();
        }
    }

    public void Clear()
    {
        Subscription?.Retire();
        Subscription = null;
        Session = null;
        Identity = string.Empty;
    }
}
