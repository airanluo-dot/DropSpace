using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LocalStorageMetricsTests
{
    [TestMethod]
    public async Task SummaryCountsOwnedFilesWithoutFollowingExternalLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-metrics-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(Path.Combine(root, "app"));
        paths.EnsureCreated();
        var external = Path.Combine(root, "external");
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "source.bin");
        await File.WriteAllBytesAsync(externalFile, new byte[1_024]);
        await File.WriteAllBytesAsync(Path.Combine(paths.Data, "owned.bin"), new byte[3]);
        var directoryLink = Path.Combine(paths.Root, "linked-directory");
        var fileLink = Path.Combine(paths.Root, "linked-file.bin");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(directoryLink, external);
                File.CreateSymbolicLink(fileLink, externalFile);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Inconclusive($"Symbolic links are unavailable: {exception.GetType().Name}");
            }

            Assert.AreEqual(3L, await new LocalStorageMetrics(paths).GetByteLengthAsync());
            Assert.AreEqual(1_024L, new FileInfo(externalFile).Length);
        }
        finally
        {
            if (Directory.Exists(directoryLink)) Directory.Delete(directoryLink);
            if (File.Exists(fileLink)) File.Delete(fileLink);
            Directory.Delete(root, recursive: true);
        }
    }
}
