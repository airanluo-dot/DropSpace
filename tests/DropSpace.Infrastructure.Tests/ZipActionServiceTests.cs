using System.IO.Compression;
using DropSpace.Core.Actions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Actions;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ZipActionServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "DropSpace-zip-audit", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task ArchivePreservesHierarchyEmptyDirectoriesAndUnicodeSourceFiles()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "中文🎵", "empty"));
        var original = Path.Combine(source, "中文🎵", "song.txt");
        await File.WriteAllTextAsync(original, "original");
        var result = await ExportAsync(Folder(source, "Album"));
        using var archive = ZipFile.OpenRead(result.OutputPaths.Single());
        CollectionAssert.AreEquivalent(new[] { "Album/", "Album/中文🎵/", "Album/中文🎵/empty/", "Album/中文🎵/song.txt" }, archive.Entries.Select(entry => entry.FullName).ToArray());
        using var reader = new StreamReader(archive.GetEntry("Album/中文🎵/song.txt")!.Open());
        Assert.AreEqual("original", await reader.ReadToEndAsync());
        Assert.AreEqual("original", await File.ReadAllTextAsync(original));
    }

    [TestMethod]
    public async Task DuplicateSelectedFolderTitlesKeepIndependentSubtrees()
    {
        var left = Path.Combine(_root, "left");
        var right = Path.Combine(_root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        await File.WriteAllTextAsync(Path.Combine(left, "file.txt"), "left");
        await File.WriteAllTextAsync(Path.Combine(right, "file.txt"), "right");
        var result = await ExportAsync(Folder(left, "Same"), Folder(right, "Same"));
        using var archive = ZipFile.OpenRead(result.OutputPaths.Single());
        CollectionAssert.AreEquivalent(new[] { "Same/", "Same/file.txt", "Same (1)/", "Same (1)/file.txt" }, archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [TestMethod]
    public async Task DotSegmentDisplayTitleCannotCreateTraversalEntries()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "safe");
        var result = await ExportAsync(Folder(source, ".."));
        using var archive = ZipFile.OpenRead(result.OutputPaths.Single());
        CollectionAssert.AreEquivalent(new[] { "item/", "item/file.txt" }, archive.Entries.Select(entry => entry.FullName).ToArray());
    }

    [TestMethod]
    public async Task RegistryRejectsSelectedDirectoryLinksWithoutPartialOutputAndStillSkipsNestedLinks()
    {
        WindowsReparseFixture.RequireSymbolicLinks(directory: true);
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var original = Path.Combine(source, "file.txt");
        var targetFile = Path.Combine(target, "target.txt");
        await File.WriteAllTextAsync(original, "original");
        await File.WriteAllTextAsync(targetFile, "linked target");
        var rootLink = Path.Combine(_root, "selected-link");
        Directory.CreateSymbolicLink(rootLink, target);
        Directory.CreateSymbolicLink(Path.Combine(source, "nested-link"), target);

        var paths = new AppStoragePaths(_root);
        var registry = new ItemActionRegistry([new ZipActionService(paths)]);
        var ordinary = Folder(source, "Source");
        var linked = Folder(rootLink, "Selected link");
        foreach (var selected in new[] { new[] { linked }, new[] { ordinary, linked } })
        {
            var selection = new ItemSelectionSnapshot(selected);
            Assert.AreEqual(ItemActionId.CompressZip, registry.Evaluate(selection).Single().Descriptor.Id);
            var result = await registry.ExecuteAsync(ItemActionId.CompressZip, new ItemActionContext(selection));
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("invalid-data", result.ErrorCategory);
            Assert.AreEqual("ActionInvalidData", result.MessageResourceKey);
            Assert.AreEqual(0, result.OutputPaths.Count);
            Assert.AreEqual(0, Directory.GetFiles(paths.Exports).Length);
            Assert.AreEqual("original", await File.ReadAllTextAsync(original));
            Assert.AreEqual("linked target", await File.ReadAllTextAsync(targetFile));
        }

        var success = await registry.ExecuteAsync(
            ItemActionId.CompressZip, new ItemActionContext(new ItemSelectionSnapshot([ordinary])));
        Assert.IsTrue(success.Succeeded);
        Assert.AreEqual("ActionCompleted", success.MessageResourceKey);
        using var archive = ZipFile.OpenRead(success.OutputPaths.Single());
        CollectionAssert.AreEquivalent(
            new[] { "Source/", "Source/file.txt" }, archive.Entries.Select(entry => entry.FullName).ToArray());
        using var reader = new StreamReader(archive.GetEntry("Source/file.txt")!.Open());
        Assert.AreEqual("original", await reader.ReadToEndAsync());
        Assert.AreEqual("original", await File.ReadAllTextAsync(original));
        Assert.AreEqual("linked target", await File.ReadAllTextAsync(targetFile));
        Assert.AreEqual(1, Directory.GetFiles(paths.Exports).Length);
    }

    [TestMethod]
    public async Task EmptyDirectoryTreeConsumesEntryBudgetAndFailedArchiveIsRemoved()
    {
        var source = Path.Combine(_root, "source");
        for (var index = 0; index < 10_000; index++)
            Directory.CreateDirectory(Path.Combine(source, index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ExportAsync(Folder(source, "Tree")));
        Assert.AreEqual(10_000, Directory.GetDirectories(source).Length);
        Assert.AreEqual(0, Directory.GetFiles(new AppStoragePaths(_root).Exports).Length);
    }

    private Task<ItemActionResult> ExportAsync(params DropItemSnapshot[] items) =>
        new ZipActionService(new AppStoragePaths(_root)).ExecuteAsync(new ItemActionContext(new ItemSelectionSnapshot(items)));

    private static DropItemSnapshot Folder(string path, string title) =>
        new(Guid.NewGuid(), ItemKind.Folder, ItemStatus.Available, title, path, null, null, null, null, null, 1);
}
