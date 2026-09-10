using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Actions;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class HashActionServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "DropSpace-hash-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task HashResultMatchesExportAndNeverOverwritesSourceOrPriorExport()
    {
        var paths = new AppStoragePaths(_root);
        paths.EnsureCreated();
        var sourcePath = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "abc", new System.Text.UTF8Encoding(false));
        var item = new DropItemSnapshot(
            Guid.NewGuid(), ItemKind.File, ItemStatus.Available, "source.txt", sourcePath,
            ".txt", 3, "text/plain", null, null, 1);
        var service = new HashActionService(paths);

        var first = await service.ExecuteAsync(new(new([item])));
        var second = await service.ExecuteAsync(new(new([item])));

        const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.AreEqual(expected, first.ResultText);
        Assert.AreNotEqual(first.OutputPaths.Single(), second.OutputPaths.Single());
        StringAssert.StartsWith(await File.ReadAllTextAsync(first.OutputPaths.Single()), expected);
        Assert.AreEqual("abc", await File.ReadAllTextAsync(sourcePath));
    }
}
