using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ReparseSafeDirectoryEnumeratorTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-enumerator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SharesTraversalBudgetAcrossRootsAndHonorsConfiguredDirectoryPaths()
    {
        var limits = new TransferLimits { MaxItems = 2, MaxTotalBytes = 1024 };
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        foreach (var root in new[] { first, second })
        {
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), "x");
            var individual = ReparseSafeDirectoryEnumerator.EnumerateSources(
                [root], limits, new TransferTraversalBudget(3));
            Assert.HasCount(1, individual);
            Assert.AreEqual(Path.GetFileName(root) + "/file.txt", individual[0].RelativePath);
        }

        var combinedFailure = Assert.ThrowsExactly<TransferPreparationLimitException>(() =>
            ReparseSafeDirectoryEnumerator.EnumerateSources([first, second], limits, new TransferTraversalBudget(4)));
        Assert.AreEqual(TransferPreparationLimitReason.TraversalEntries, combinedFailure.Reason);
        Assert.AreEqual(4L, combinedFailure.Limit,
            "The same preparation budget must count both selected roots and empty children.");

        var emptyRoot = Path.Combine(_root, new string('r', 60));
        Directory.CreateDirectory(Path.Combine(emptyRoot, "empty"));
        var shortPaths = limits with { MaxRelativePathLength = 64 };
        var emptyPathFailure = Assert.ThrowsExactly<TransferPreparationLimitException>(() =>
            ReparseSafeDirectoryEnumerator.EnumerateSources([emptyRoot], shortPaths, new TransferTraversalBudget(10)));
        Assert.AreEqual(TransferPreparationLimitReason.RelativePathLength, emptyPathFailure.Reason);
        Assert.AreEqual(64L, emptyPathFailure.Limit,
            "An empty child directory still consumes the configured relative-path budget.");

        var deepRoot = Path.Combine(_root, "deep");
        var leafDirectory = deepRoot;
        for (var depth = 0; depth < 40; depth++) leafDirectory = Path.Combine(leafDirectory, new string('d', 26));
        Directory.CreateDirectory(leafDirectory);
        var leaf = Path.Combine(leafDirectory, "leaf.txt");
        await File.WriteAllTextAsync(leaf, "deep");
        var configuredPaths = limits with { MaxRelativePathLength = 2048 };
        var deepEntries = ReparseSafeDirectoryEnumerator.EnumerateSources(
            [deepRoot], configuredPaths, new TransferTraversalBudget(100));
        Assert.HasCount(1, deepEntries);
        Assert.AreEqual(leaf, deepEntries[0].FullPath);
        Assert.AreEqual(41, deepEntries[0].RelativePath.Count(character => character == '/'));
        Assert.IsTrue(deepEntries[0].RelativePath.Length > TransferLimits.DefaultMaxRelativePathLength &&
            deepEntries[0].RelativePath.Length <= configuredPaths.MaxRelativePathLength,
            "A valid configured path above 1024 and deeper than 32 levels must remain enumerable.");
        var defaultPathFailure = Assert.ThrowsExactly<TransferPreparationLimitException>(() =>
            ReparseSafeDirectoryEnumerator.EnumerateSources([deepRoot], limits, new TransferTraversalBudget(100)));
        Assert.AreEqual(TransferPreparationLimitReason.RelativePathLength, defaultPathFailure.Reason);
        Assert.AreEqual((long)TransferLimits.DefaultMaxRelativePathLength, defaultPathFailure.Limit);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            ReparseSafeDirectoryEnumerator.EnumerateSources([first], limits, new TransferTraversalBudget(4), cancellation.Token));
    }

    [TestMethod]
    public async Task EnumeratesNestedRegularFilesWithStableManifestPaths()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(_root, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(_root, "nested", "child.txt"), "child");

        var entries = ReparseSafeDirectoryEnumerator.Enumerate(_root, "folder", 10, 1024);

        Assert.AreEqual(2, entries.Count);
        CollectionAssert.AreEquivalent(
            new[] { "folder/root.txt", "folder/nested/child.txt" },
            entries.Select(entry => entry.RelativePath).ToArray());
    }

    [TestMethod]
    public async Task EnforcesItemAndByteLimitsBeforeReturningAnIncompleteManifest()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "one.txt"), "1234");
        await File.WriteAllTextAsync(Path.Combine(_root, "two.txt"), "5678");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ReparseSafeDirectoryEnumerator.Enumerate(_root, "folder", 1, 1024));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ReparseSafeDirectoryEnumerator.Enumerate(_root, "folder", 10, 5));
    }

    [TestMethod]
    public void RejectsAReparsePointSelectedAsTheRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Reparse-point behavior is a Windows-only boundary.");
        }

        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Inconclusive("The Windows test host cannot create a directory reparse point.");
        }
        catch (IOException)
        {
            Assert.Inconclusive("The Windows test host cannot create a directory reparse point.");
        }

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ReparseSafeDirectoryEnumerator.Enumerate(link, "link", 10, 1024));
    }
}
