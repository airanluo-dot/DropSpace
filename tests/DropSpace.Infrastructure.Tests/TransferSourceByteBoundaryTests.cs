using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class TransferSourceByteBoundaryTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-transfer-byte-boundary", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task FullByteBudgetStillAllowsSelectedDirectoryContainingZeroByteFiles()
    {
        var (first, directory, child) = await SourcesAsync([]);
        var limits = new TransferLimits { MaxItems = 2, MaxTotalBytes = 1 };

        var entries = ReparseSafeDirectoryEnumerator.EnumerateSources(
            [first, directory], limits, new TransferTraversalBudget(3));

        Assert.HasCount(2, entries);
        Assert.AreEqual(1L, entries.Sum(entry => entry.Length));
        Assert.AreEqual(child, entries[1].FullPath);
        Assert.AreEqual(0L, entries[1].Length);
        Assert.AreEqual("directory/child.bin", entries[1].RelativePath);
        CollectionAssert.AreEqual(new byte[] { 42 }, await File.ReadAllBytesAsync(first));
        Assert.AreEqual(0L, new FileInfo(child).Length);
    }

    [TestMethod]
    public async Task FullByteBudgetStillRejectsSelectedDirectoryWithAnyPositiveByteChild()
    {
        var (first, directory, child) = await SourcesAsync([99]);
        var limits = new TransferLimits { MaxItems = 2, MaxTotalBytes = 1 };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ReparseSafeDirectoryEnumerator.EnumerateSources(
                [first, directory], limits, new TransferTraversalBudget(3)));

        CollectionAssert.AreEqual(new byte[] { 42 }, await File.ReadAllBytesAsync(first));
        CollectionAssert.AreEqual(new byte[] { 99 }, await File.ReadAllBytesAsync(child));
    }

    private async Task<(string First, string Directory, string Child)> SourcesAsync(byte[] childBytes)
    {
        var directory = Path.Combine(_root, "directory");
        Directory.CreateDirectory(directory);
        var first = Path.Combine(_root, "first.bin");
        var child = Path.Combine(directory, "child.bin");
        await File.WriteAllBytesAsync(first, [42]);
        await File.WriteAllBytesAsync(child, childBytes);
        return (first, directory, child);
    }
}
