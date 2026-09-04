namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DropLinkChunkLedgerTests
{
    [TestMethod]
    public async Task OneHundredParallelUniqueChunksHaveExactProgressAndNoDoubleCount()
    {
        var ledger = new DropLinkChunkLedger();
        var itemId = Guid.NewGuid();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 100)
                .Select(index => Task.Run(() => ledger.TryAdd(itemId, index, 4L))));

        Assert.IsTrue(results.All(result => result));
        Assert.AreEqual(100, ledger.GetItemCount(itemId));
        Assert.AreEqual(400L, ledger.TransferredBytes);

        Assert.IsFalse(ledger.TryAdd(itemId, 0, 4L));
        Assert.AreEqual(400L, ledger.TransferredBytes);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 100).ToArray(),
            ledger.Snapshot()[itemId].ToArray());
    }
}
