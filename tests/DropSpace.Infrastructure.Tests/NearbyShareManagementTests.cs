using System.Net;
using System.Reflection;
using System.Text.Json;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Sharing;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class NearbyShareManagementTests
{
    [TestMethod]
    public async Task MetadataRevokeAndExpiryFollowTheRealShareRegistryWithoutReadSideStartup()
    {
        var address = LocalNetworkInterfaceResolver.Resolve();
        var clock = new ManualShareClock();
        var resolves = 0;
        await using var server = new NearbyShareServer(null, () =>
        {
            Interlocked.Increment(ref resolves);
            return address;
        }, clock);
        var notifications = 0;
        server.SharesChanged += (_, _) => Interlocked.Increment(ref notifications);
        var empty = server.GetSnapshot();
        Assert.HasCount(0, empty.Items);
        Assert.IsNull(server.GetDescriptor(Guid.NewGuid()));
        Assert.IsNull(server.BaseUri);
        Assert.AreEqual(0, resolves, "Management reads must not request an address or start the listener.");
        Assert.AreEqual(0, clock.ActiveTimers);

        byte[] payload = [1, 2, 3];
        var item = new NearbyShareItem(Guid.NewGuid(), "fixture.txt", "text/plain", payload.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        var first = await server.CreateShareAsync([item], TimeSpan.FromMinutes(1));
        var created = server.GetSnapshot();
        Assert.HasCount(1, created.Items);
        var summary = created.Items[0];
        Assert.AreEqual(first.ShareId, summary.ShareId);
        Assert.AreEqual(item.DisplayName, summary.DisplayName);
        Assert.AreEqual(1, summary.ItemCount);
        Assert.AreEqual(3L, summary.TotalBytes);
        Assert.AreEqual(first.ExpiresAtUtc, summary.ExpiresAtUtc);
        Assert.IsTrue(created.Revision > empty.Revision && notifications > 0);
        var metadataJson = JsonSerializer.Serialize(created);
        Assert.DoesNotContain(first.Url.ToString(), metadataJson);
        Assert.DoesNotContain(first.Url.Segments[^1], metadataJson);
        Assert.AreEqual(first, server.GetDescriptor(first.ShareId));
        var fileUrl = new Uri(first.Url + "/file/" + item.Id.ToString("N"));
        CollectionAssert.AreEqual(payload, await client.GetByteArrayAsync(fileUrl));

        var beforeRevoke = notifications;
        Assert.IsTrue(server.Revoke(first.ShareId));
        Assert.IsTrue(notifications > beforeRevoke);
        Assert.HasCount(0, server.GetSnapshot().Items);
        Assert.IsNull(server.GetDescriptor(first.ShareId));
        using (var denied = await client.GetAsync(fileUrl))
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode,
                "Revocation must reject a later request that previously passed the actual private-receiver check.");
        Assert.IsFalse(server.Revoke(first.ShareId));
        Assert.AreEqual(0, clock.ActiveTimers);

        var early = await server.CreateShareAsync([item], TimeSpan.FromMinutes(1));
        var later = await server.CreateShareAsync([item], TimeSpan.FromMinutes(2));
        Assert.AreEqual(1, clock.CreatedTimers, "Share publication must reuse one expiry timer.");
        Assert.AreEqual(1, clock.ActiveTimers);
        Assert.AreEqual(early.ExpiresAtUtc, clock.NextDueAt);
        var beforeExpiry = notifications;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsTrue(notifications > beforeExpiry,
            "Expiry must publish without relying on a later snapshot read to prune entries.");
        var afterEarly = server.GetSnapshot();
        Assert.HasCount(1, afterEarly.Items);
        Assert.AreEqual(later.ShareId, afterEarly.Items[0].ShareId);
        Assert.IsNull(server.GetDescriptor(early.ShareId));
        Assert.AreEqual(later, server.GetDescriptor(later.ShareId));
        Assert.AreEqual(later.ExpiresAtUtc, clock.NextDueAt);
        beforeExpiry = notifications;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsTrue(notifications > beforeExpiry);
        Assert.HasCount(0, server.GetSnapshot().Items);
        Assert.IsNull(server.GetDescriptor(later.ShareId));
        Assert.AreEqual(0, clock.ActiveTimers, "An empty registry must not keep an expiry timer armed.");

        var networkShare = await server.CreateShareAsync([item]);
        var beforeNetwork = notifications;
        typeof(NearbyShareServer).GetMethod("NetworkChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [null, EventArgs.Empty]);
        Assert.IsTrue(notifications > beforeNetwork);
        Assert.HasCount(0, server.GetSnapshot().Items);
        Assert.IsNull(server.GetDescriptor(networkShare.ShareId));
        Assert.IsNull(server.BaseUri);
        Assert.AreEqual(0, clock.ActiveTimers);
        await server.DisposeAsync();
        Assert.IsTrue(clock.AllTimersDisposed);
        Assert.HasCount(0, server.GetSnapshot().Items);
        Assert.IsNull(server.GetDescriptor(networkShare.ShareId));
    }

    private sealed class ManualShareClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal int CreatedTimers { get { lock (_gate) return _timers.Count; } }
        internal int ActiveTimers { get { lock (_gate) return _timers.Count(timer => timer.DueAt is not null); } }
        internal bool AllTimersDisposed { get { lock (_gate) return _timers.All(timer => timer.Disposed); } }
        internal DateTimeOffset? NextDueAt
        {
            get { lock (_gate) return _timers.Select(timer => timer.DueAt).Where(due => due is not null).Min(); }
        }

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += elapsed;
                due = _timers.Where(timer => timer.DueAt <= _now).ToArray();
                foreach (var timer in due) timer.DueAt = null;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualShareClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal DateTimeOffset? DueAt { get; set; }
            internal bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.AreEqual(Timeout.InfiniteTimeSpan, period, "The real expiry scheduler must request a one-shot timer.");
                lock (owner._gate)
                {
                    if (Disposed) return false;
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now.Add(dueTime);
                    return true;
                }
            }
            internal void Fire()
            {
                lock (owner._gate) { if (Disposed) return; }
                callback(state);
            }
            public void Dispose()
            {
                lock (owner._gate) { Disposed = true; DueAt = null; }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
