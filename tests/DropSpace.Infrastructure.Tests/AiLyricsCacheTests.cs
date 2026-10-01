using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AiLyricsCacheTests
{
    [TestMethod]
    public async Task ClearRemovesOnlyOwnedAiEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new AiLyricsCache(root);
            var key = new string('A', 64);
            await cache.WriteAsync(key, "[]", CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(root, "unrelated.json"), "keep");
            await cache.ClearAsync(CancellationToken.None);
            Assert.IsNull(await cache.ReadAsync(key, CancellationToken.None));
            Assert.IsTrue(File.Exists(Path.Combine(root, "unrelated.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TrimmingAccountsForFilesBeyondTheFirstTenThousand()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Every 10,000-file subset is below budget, while the complete directory
            // is above it. Truncating enumeration before accounting cannot evict.
            for (var index = 0; index < 10_001; index++)
            {
                using var file = File.Create(Path.Combine(root, index.ToString("X64", System.Globalization.CultureInfo.InvariantCulture) + ".json"));
                file.SetLength(10_485);
            }
            await new AiLyricsCache(root).WriteAsync(new string('F', 64), "[]", CancellationToken.None);
            var bytes = Directory.EnumerateFiles(root, "*.json").Sum(path => new FileInfo(path).Length);
            Assert.IsTrue(bytes <= 100L * 1024 * 1024, $"Cache remained above budget: {bytes} bytes.");
        }
        finally { Directory.Delete(root, true); }
    }
}
