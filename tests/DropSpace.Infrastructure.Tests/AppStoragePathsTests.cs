using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AppStoragePathsTests
{
    [TestMethod]
    public void CurrentUserStorageIsRootedBelowLocalAppData()
    {
        var paths = AppStoragePaths.CreateForCurrentUser();
        var localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        Assert.IsTrue(Path.IsPathRooted(paths.Root));
        Assert.IsTrue(paths.Root.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("DropSpace", Path.GetFileName(paths.Root));
        Assert.AreEqual(Path.Combine(paths.Root, "data", "dropspace.db"), paths.Database);
        Assert.AreEqual(Path.Combine(paths.Root, "exports"), paths.Exports);
    }
}
