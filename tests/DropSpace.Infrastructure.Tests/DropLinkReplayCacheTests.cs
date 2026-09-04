using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DropLinkReplayCacheTests
{
    [TestMethod]
    public void HandoffReplayIsBoundedIdempotentAndExpiring()
    {
        var cache = new DropLinkReplayCache();
        var peer = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var firstSession = Guid.NewGuid();

        Assert.IsTrue(cache.TryReserve(peer, firstSession, now));
        Assert.IsFalse(cache.TryReserve(peer, firstSession, now));

        for (var index = 1; index < DropLinkProtocolPolicy.MaximumHandoffReplayEntriesPerPeer; index++)
        {
            Assert.IsTrue(cache.TryReserve(peer, Guid.NewGuid(), now));
        }

        Assert.IsFalse(cache.TryReserve(peer, Guid.NewGuid(), now));
        Assert.AreEqual(DropLinkProtocolPolicy.MaximumHandoffReplayEntriesPerPeer, cache.Count);

        Assert.IsTrue(cache.TryReserve(
            Guid.NewGuid(),
            Guid.NewGuid(),
            now + DropLinkProtocolPolicy.HandoffReplayRetention + TimeSpan.FromSeconds(1)));
        Assert.AreEqual(1, cache.Count);
    }
}
