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
