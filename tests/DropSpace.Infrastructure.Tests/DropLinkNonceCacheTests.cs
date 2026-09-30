using DropSpace.Infrastructure.Network;
using DropSpace.Core.Transfer;
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

    [TestMethod]
    public void DefaultMaximumTransferAndFullApprovalWaitPreserveReplayProtection()
    {
        var cache = new DropLinkNonceCache();
        var peer = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var polls = (int)(TimeSpan.FromMinutes(5).TotalMilliseconds / 500);
        var chunks = (int)Math.Ceiling(TransferLimits.DefaultMaxTotalBytes / (double)TransferLimits.DefaultChunkBytes);
        var requests = polls + chunks + 3; // offer, acceptance control, completion
        for (var index = 0; index < requests; index++)
        {
            Assert.IsTrue(cache.TryReserve(peer, $"flow-{index}", now.AddMilliseconds(index)),
                $"Default request {index} must fit inside the retained replay budget.");
        }
        Assert.AreEqual(requests, cache.Count);
        Assert.IsFalse(cache.TryReserve(peer, "flow-0", now.AddMinutes(9)), "An early request must remain rejected throughout the replay window.");
        Assert.IsFalse(cache.TryReserve(peer, $"flow-{requests - 1}", now.AddMinutes(9)));
    }
}
