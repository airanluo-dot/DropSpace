using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LocalStorageMetricsTests
{
    [TestMethod]
    public async Task CacheAndDlcUsagePartitionPackagesWhileKeepingLyricsInCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-metrics-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        try
        {
            var files = new Dictionary<string, int>
            {
                ["payloads/clipboard.bin"] = 3, ["cache/lyrics/song.json"] = 7,
                ["AiLyrics/Cache/translation.json"] = 11, ["data/settings.json"] = 13,
                ["AiLyrics/Models/model.bin"] = 17, ["AiLyrics/Runtime/cuda.zip"] = 19,
                ["AiLyrics/Staging/partial.download"] = 23, ["Modules/packages/tool/worker.exe"] = 29,
                ["Modules/data/tool/settings.json"] = 31, ["NeteaseEnhancement/installer.exe"] = 37,
            };
            foreach (var (relative, size) in files)
            {
                var filename = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
                await File.WriteAllBytesAsync(filename, new byte[size]);
            }
            var metrics = new LocalStorageMetrics(paths);
            Assert.AreEqual(34L, await metrics.GetByteLengthAsync());
            Assert.AreEqual(156L, await metrics.GetDlcByteLengthAsync());
            Assert.AreEqual(files.Values.Sum(), (await metrics.GetByteLengthAsync()) + (await metrics.GetDlcByteLengthAsync()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
