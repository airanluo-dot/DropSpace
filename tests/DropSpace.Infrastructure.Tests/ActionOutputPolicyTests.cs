using DropSpace.Infrastructure.Actions;
using DropSpace.Infrastructure.Storage;
using DropSpace.Core.Actions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ActionOutputPolicyTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-action-output", Guid.NewGuid().ToString("N"));
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
    public void MissingDestinationUsesDedicatedExportsDirectory()
    {
        var paths = new AppStoragePaths(_root);

        Assert.AreEqual(paths.Exports, ActionOutputPolicy.ResolveDirectory(paths, null));
        Assert.AreEqual(paths.Exports, ActionOutputPolicy.ResolveDirectory(paths, ""));
    }

    [TestMethod]
    public void OutputNamesAreReservedWithoutOverwritingExistingFiles()
    {
        using var first = ActionOutputPolicy.CreateNewFile(_root, "report", ".txt", out var firstPath);
        using var second = ActionOutputPolicy.CreateNewFile(_root, "report", ".txt", out var secondPath);

        Assert.AreEqual(Path.Combine(_root, "report.txt"), firstPath);
        Assert.AreEqual(Path.Combine(_root, "report (1).txt"), secondPath);
        Assert.AreNotEqual(firstPath, secondPath);
    }

    [TestMethod]
    [DataRow("CON.txt")]
    [DataRow("con")]
    [DataRow("LPT9.")]
    public void WindowsReservedDeviceNamesAreMadeSafe(string value)
    {
        var sanitized = ActionOutputPolicy.SanitizeFileName(value, "fallback");

        Assert.IsFalse(string.Equals(sanitized.Split('.')[0], value.TrimEnd('.', ' '), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(sanitized.EndsWith(' '));
        Assert.IsFalse(sanitized.EndsWith('.'));
    }

    [TestMethod]
    public void LongStemsAreBoundedForCollisionAndExtensionBudget()
    {
        var sanitized = ActionOutputPolicy.SanitizeFileName(new string('x', 512), "fallback");

        Assert.IsTrue(sanitized.Length <= 160);
    }

    [TestMethod]
    public async Task HashActionWritesASeparateExportAndPreservesTheSource()
    {
        var paths = new AppStoragePaths(_root);
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "sample.bin");
        var sourceBytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var item = new DropItemSnapshot(
            Guid.NewGuid(),
            ItemKind.File,
            ItemStatus.Available,
            "sample.bin",
            sourcePath,
            ".bin",
            sourceBytes.Length,
            null,
            null,
            null,
            1);

        var result = await new HashActionService(paths).ExecuteAsync(
            new ItemActionContext(new ItemSelectionSnapshot([item]), _root));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.OutputPaths.Count);
        Assert.IsTrue(File.Exists(result.OutputPaths[0]));
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.AreNotEqual(sourcePath, result.OutputPaths[0]);
    }
}
