using DropSpace.App.Services;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ShareFolderEnumerationTests
{
    [TestMethod]
    public async Task EmptyDirectoryTreeIsBoundedAndCancelledWorkDoesNotScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-share-tree", Guid.NewGuid().ToString("N"));
        try
        {
            for (var index = 0; index < 5; index++) Directory.CreateDirectory(Path.Combine(root, index.ToString()));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ShareFolderEnumeration.EnumerateAsync(root, CancellationToken.None, maximumEntries: 4));
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => ShareFolderEnumeration.EnumerateAsync(root, stop.Token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DepthBudgetRejectsBeforeUnboundedQueueGrowth()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-share-tree", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "one", "two", "three"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ShareFolderEnumeration.EnumerateAsync(root, CancellationToken.None, maximumDepth: 2));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
