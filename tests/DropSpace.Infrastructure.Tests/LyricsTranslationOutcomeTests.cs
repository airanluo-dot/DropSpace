using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsTranslationOutcomeTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly LyricsQuery Query = new("Song", "Artist", "", TimeSpan.FromSeconds(3));
    private static readonly LyricsDocument Source = new([new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "Hello", null, [])], LyricsProviderKind.LocalLrc);

    [TestMethod]
    public async Task AllCopyIsNeutralUncachedAndSessionSuppressedUntilClear()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new AiLyricsCache(root); var coordinator = new LyricsTranslationCoordinator(cache); var calls = 0;
            Task<string> Infer(string _, IReadOnlyList<int> ids, CancellationToken token) { calls++; return Task.FromResult("[{\"id\":0,\"text\":\"Hello\"}]"); }
            var result = await coordinator.TranslateBatchesDetailedAsync(Query, Source, "en", Hash, Infer, default);
            Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
            Assert.AreSame(Source, result.Document);
            Assert.IsFalse(Directory.Exists(root));
            Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, (await coordinator.TryGetCachedResultAsync(Query, Source, "en", Hash, default))!.Outcome);
            await coordinator.TranslateBatchesDetailedAsync(Query, Source, "en", Hash, Infer, default);
            Assert.AreEqual(1, calls);
            await cache.ClearAsync(default);
            Assert.IsNull(await coordinator.TryGetCachedResultAsync(Query, Source, "en", Hash, default));
            await coordinator.TranslateBatchesDetailedAsync(Query, Source, "en", Hash, Infer, default);
            Assert.AreEqual(2, calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task HistoricalAllCopyEntryCannotBecomeCompleted()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new AiLyricsCache(root);
            await cache.WriteAsync(LyricsTranslationPrompt.CacheKey(Query, Source, "en", Hash), "[{\"id\":0,\"text\":\"Hello\"}]", default);
            var coordinator = new LyricsTranslationCoordinator(cache);
            var result = await coordinator.TryGetCachedResultAsync(Query, Source, "en", Hash, default);
            Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result!.Outcome);
            Assert.AreSame(Source, result.Document);
            Assert.IsNull(await coordinator.TryGetCachedAsync(Query, Source, "en", Hash, default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MixedCopyAndUsefulTranslationRemainsAReusableSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var source = Source with { Lines = [Source.Lines[0], Source.Lines[0] with { Text = "Goodbye" }] };
            var coordinator = new LyricsTranslationCoordinator(new(root));
            var result = await coordinator.TranslateBatchesDetailedAsync(Query, source, "zh", Hash,
                (_, _, _) => Task.FromResult("[{\"id\":0,\"text\":\"Hello\"},{\"id\":1,\"text\":\"再见\"}]"), default);
            Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
            Assert.IsNull(result.Document.Lines[0].Secondary);
            Assert.AreEqual("再见", result.Document.Lines[1].Secondary);
            var restarted = new LyricsTranslationCoordinator(new(root));
            Assert.AreEqual(LyricsTranslationOutcome.Translated, (await restarted.TryGetCachedResultAsync(Query, source, "zh", Hash, default))!.Outcome);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InvalidCancelledAndTimedOutAttemptsAreNeverNegativeCached()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var coordinator = new LyricsTranslationCoordinator(new(root)); var calls = 0;
            var failed = await coordinator.TranslateBatchesDetailedAsync(Query, Source, "zh", Hash,
                (_, _, _) => { calls++; return Task.FromResult("invalid"); }, default);
            Assert.AreEqual(LyricsTranslationOutcome.Failed, failed.Outcome); Assert.AreEqual(2, calls);
            Assert.IsNull(await coordinator.TryGetCachedResultAsync(Query, Source, "zh", Hash, default));
            await Assert.ThrowsAsync<TimeoutException>(() => coordinator.TranslateBatchesDetailedAsync(Query, Source, "zh", Hash,
                (_, _, _) => throw new TimeoutException(), default));
            Assert.IsNull(await coordinator.TryGetCachedResultAsync(Query, Source, "zh", Hash, default));
            using var stop = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.TranslateBatchesDetailedAsync(Query, Source, "zh", Hash,
                (_, _, _) => { stop.Cancel(); return Task.FromResult("[{\"id\":0,\"text\":\"Hello\"}]"); }, stop.Token));
            Assert.IsNull(await coordinator.TryGetCachedResultAsync(Query, Source, "zh", Hash, default));
            Assert.IsFalse(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task IdentifiedProviderTranslationRetainsPriority()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Source with { Lines = [Source.Lines[0] with { Secondary = "你好", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-CN" }] };
        var result = await new LyricsTranslationCoordinator(new(root)).TranslateBatchesDetailedAsync(Query, source, "zh", Hash,
            (_, _, _) => throw new AssertFailedException("Provider translation must suppress inference."), default);
        Assert.AreSame(source, result.Document);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task NegativeSessionCacheIsBoundedToSixtyFourIdentities()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var coordinator = new LyricsTranslationCoordinator(new(root));
        for (var index = 0; index < 65; index++)
            await coordinator.TranslateBatchesDetailedAsync(Query with { TrackIdentity = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }, Source, "en", Hash,
                (_, _, _) => Task.FromResult("[{\"id\":0,\"text\":\"Hello\"}]"), default);
        Assert.IsNull(await coordinator.TryGetCachedResultAsync(Query with { TrackIdentity = "0" }, Source, "en", Hash, default));
        Assert.IsNotNull(await coordinator.TryGetCachedResultAsync(Query with { TrackIdentity = "64" }, Source, "en", Hash, default));
        Assert.IsFalse(Directory.Exists(root));
    }
}
