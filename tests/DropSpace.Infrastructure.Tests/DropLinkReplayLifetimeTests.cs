using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DropLinkReplayLifetimeTests
{
    [TestMethod]
    public void FutureDatedHandoffRemainsReservedThroughoutItsAcceptedLifetime()
    {
        var cache = new DropLinkReplayCache();
        var peer = Guid.NewGuid();
        var session = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var createdAt = now + DropLinkProtocolPolicy.HandoffMaximumFutureSkew;
        Assert.IsTrue(cache.TryReserve(peer, session, now, createdAt));

        // The old receipt-based reservation expired here even though the host
        // would still accept the message's timestamp.
        var replayAt = now + DropLinkProtocolPolicy.HandoffReplayRetention + TimeSpan.FromSeconds(1);
        Assert.IsTrue(createdAt >= replayAt - DropLinkProtocolPolicy.HandoffReplayRetention);
        Assert.IsFalse(cache.TryReserve(peer, session, replayAt, createdAt));
        Assert.AreEqual(1, cache.Count);

        var boundary = createdAt + DropLinkProtocolPolicy.HandoffReplayRetention;
        Assert.IsFalse(cache.TryReserve(peer, session, boundary, createdAt),
            "The timestamp is valid at the inclusive expiry boundary, so replay must still be rejected.");
        var expiredAt = boundary + TimeSpan.FromSeconds(1);
        Assert.IsTrue(cache.TryReserve(peer, session, expiredAt, expiredAt));
        Assert.AreEqual(1, cache.Count, "A fully expired reservation must not prevent later bounded reuse.");
    }

    [TestMethod]
    public void InvalidTimestampCannotExtendOrConsumeReplayCapacity()
    {
        var cache = new DropLinkReplayCache();
        var now = DateTimeOffset.UtcNow;
        var peer = Guid.NewGuid();
        Assert.IsFalse(cache.TryReserve(peer, Guid.NewGuid(), now,
            now + DropLinkProtocolPolicy.HandoffMaximumFutureSkew + TimeSpan.FromSeconds(1)));
        Assert.IsFalse(cache.TryReserve(peer, Guid.NewGuid(), now,
            now - DropLinkProtocolPolicy.HandoffReplayRetention - TimeSpan.FromSeconds(1)));
        Assert.AreEqual(0, cache.Count);
    }
}
