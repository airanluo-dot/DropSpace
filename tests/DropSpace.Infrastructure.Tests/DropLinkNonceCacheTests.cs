using DropSpace.Infrastructure.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DropLinkNonceCacheTests
{
    [TestMethod]
    public void Cache_RejectsReplayAndBoundsEachKnownPeer()
    {
        var cache = new DropLinkNonceCache();
        var peer = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.IsTrue(cache.TryReserve(peer, "first", now));
        Assert.IsFalse(cache.TryReserve(peer, "first", now));
        for (var index = 1; index < DropLinkNonceCache.MaximumEntriesPerPeer; index++)
        {
            Assert.IsTrue(cache.TryReserve(peer, $"nonce-{index}", now));
        }

        Assert.IsFalse(cache.TryReserve(peer, "overflow", now));
        Assert.AreEqual(DropLinkNonceCache.MaximumEntriesPerPeer, cache.Count);
    }

    [TestMethod]
    public void Cache_ExpiresEntriesAndNeverGrowsForInvalidPeerOrNonce()
    {
        var cache = new DropLinkNonceCache();
        var peer = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.IsFalse(cache.TryReserve(Guid.Empty, "unknown", now));
        Assert.IsFalse(cache.TryReserve(peer, new string('x', 257), now));
        Assert.IsTrue(cache.TryReserve(peer, "old", now));
        Assert.IsTrue(cache.TryReserve(peer, "new", now.Add(DropLinkNonceCache.Retention + TimeSpan.FromSeconds(1))));
        Assert.AreEqual(1, cache.Count);
    }
}
