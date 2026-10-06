using DropSpace.App.Services;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class Beta15CacheSettingsTests
{
    [TestMethod]
    public async Task SuccessfulSavePublishesCacheOffBeforeReturningAndFailedSaveDoesNotReenableIt()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-Beta15-cache-settings-" + Guid.NewGuid().ToString("N")));
        try
        {
            var store = new JsonSettingsService(paths);
            var cache = new LyricsCache(paths.Lyrics);
            var ai = new AiLyricsCache(cache);
            var source = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "A lyric line", null, [])], LyricsProviderKind.NetEase)
                .Bind(new("song", "artist", "album", TimeSpan.FromSeconds(3)), "song", "artist", "album", 3, 12, "candidate");
            var original = new AppSettings();
            await store.SaveAsync(original);
            using var coordinator = new SettingsApplicationCoordinator(
                store, null!, null!, null!, null!, null!, NullLogger<SettingsApplicationCoordinator>.Instance, lyricsCache: cache);
            await coordinator.LoadAsync();
            await cache.WriteDocumentAsync("old", source, cache.Generation, default);
            var files = Directory.GetFiles(paths.Lyrics);
            var before = cache.Generation;
            var execution = cache.ExecutionGeneration;
            var disabled = original with { Lyrics = original.Lyrics with { CacheMaximumBytes = 0 } };
            await coordinator.SaveAsync(disabled);
            Assert.AreEqual(0L, (await store.LoadAsync()).Lyrics.CacheMaximumBytes);
            Assert.IsTrue(cache.Generation > before);
            Assert.AreEqual(execution, cache.ExecutionGeneration);
            Assert.IsNull(await cache.ReadDocumentAsync("old", default));
            await ai.WriteAsync(new string('a', 64), "[]", before, default);
            CollectionAssert.AreEquivalent(files, Directory.GetFiles(paths.Lyrics));
            if (OperatingSystem.IsWindows())
            {
                using var held = new FileStream(paths.Settings, FileMode.Open, FileAccess.Read, FileShare.Read);
                Exception? saveFailure = null;
                try { await coordinator.SaveAsync(original); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { saveFailure = error; }
                Assert.IsNotNull(saveFailure, "The held settings file must reject replacement on Windows.");
                Assert.IsNull(await cache.ReadDocumentAsync("old", default), "A failed save must retain the last successfully persisted cache-off policy.");
            }
            await store.SaveAsync(original);
            await coordinator.LoadAsync();
            Assert.IsNotNull(await cache.ReadDocumentAsync("old", default));
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true); }
    }
}
