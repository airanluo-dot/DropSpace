using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AiLyricsCacheTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public async Task CacheSurvivesRestartAndClearRemovesOnlyOwnedEntries()
    {
        var root = TemporaryRoot();
        try
        {
            await new AiLyricsCache(root).WriteAsync(Key, "[]", default);
            await File.WriteAllTextAsync(Path.Combine(root, "model.gguf"), "keep");
            var restarted = new AiLyricsCache(root);
            Assert.AreEqual("[]", await restarted.ReadAsync(Key, default));
            await restarted.ClearAsync(default);
            Assert.IsNull(await restarted.ReadAsync(Key, default));
            Assert.IsTrue(File.Exists(Path.Combine(root, "model.gguf")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ClearGenerationFencesAStaleInflightWrite()
    {
        var root = TemporaryRoot();
        try
        {
            var unified = new LyricsCache(root);
            var cache = new AiLyricsCache(unified);
            var staleGeneration = cache.Generation;
            await cache.ClearAsync(default);
            await cache.WriteAsync(Key, "[]", staleGeneration, default);
            Assert.IsNull(await cache.ReadAsync(Key, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ConcurrentAtomicWritesNeverExposePartialJson()
    {
        var root = TemporaryRoot();
        try
        {
            var cache = new AiLyricsCache(new LyricsCache(root));
            await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
                cache.WriteAsync(Key, $"[{{\"id\":0,\"text\":\"{index}\"}}]", default)));
            var saved = await cache.ReadAsync(Key, default);
            using var json = System.Text.Json.JsonDocument.Parse(saved!);
            Assert.AreEqual(1, json.RootElement.GetArrayLength());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DiskFailureDoesNotLeaveTemporaryOrCacheFiles()
    {
        var parent = TemporaryRoot();
        var root = Path.Combine(parent, "not-a-directory");
        await File.WriteAllTextAsync(root, "occupied");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new AiLyricsCache(root).WriteAsync(Key, "[]", default));
            CollectionAssert.AreEquivalent(new[] { root }, Directory.GetFiles(parent));
        }
        finally { Directory.Delete(parent, true); }
    }

    [TestMethod]
    public async Task QuotaEvictsLeastRecentlyUsedOwnedEntry()
    {
        var root = TemporaryRoot();
        try
        {
            var old = Path.Combine(root, "source-" + new string('B', 64) + ".lyrics-cache");
            await using (var file = File.Create(old)) file.SetLength(100L * 1024 * 1024);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
            var cache = new LyricsCache(root);
            await cache.SetMaximumBytesAsync(100L * 1024 * 1024);
            await new AiLyricsCache(cache).WriteAsync(Key, "[]", default);
            Assert.IsFalse(File.Exists(old));
            Assert.AreEqual("[]", await new AiLyricsCache(cache).ReadAsync(Key, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SettingsBoundQuotaFromOneHundredMiBToFiveGiB()
    {
        var low = new AppSettings { Lyrics = new() { CacheMaximumBytes = 1 } }.Validate();
        var high = new AppSettings { Lyrics = new() { CacheMaximumBytes = long.MaxValue } }.Validate();
        Assert.AreEqual(100L * 1024 * 1024, low.Lyrics.CacheMaximumBytes);
        Assert.AreEqual(5L * 1024 * 1024 * 1024, high.Lyrics.CacheMaximumBytes);
        Assert.AreEqual(1L * 1024 * 1024 * 1024, new AppSettings().Lyrics.CacheMaximumBytes);
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
