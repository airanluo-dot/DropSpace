using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PlainHyLyricsProgressTests
{
    [TestMethod]
    public async Task CurrentUpcomingThenRemainingLinesPublishImmediatelyAndCacheOnlyWhenComplete()
    {
        using var fixture = new Fixture();
        var source = fixture.Source;
        var updates = new List<LyricsTranslationProgress>();
        var order = new List<int>();
        var position = TimeSpan.FromSeconds(2.5);
        var progress = new LyricsTranslationProgressContext(() => position, () => true, async (update, _) =>
        {
            Assert.IsTrue(update.IsCurrent);
            Assert.IsTrue(update.IsEphemeral);
            Assert.AreEqual("test-request", update.RequestIdentity);
            Assert.AreEqual(fixture.Cache.Generation, update.CacheGeneration);
            Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key, default), "No partial song may become a durable entry.");
            updates.Add(update);
            if (updates.Count == 1) position = TimeSpan.FromSeconds(4.5);
            if (updates.Count == 2) position = TimeSpan.FromSeconds(0.5);
        }, "test-request");
        var result = await fixture.Translate(async (prompt, _) =>
        {
            var id = int.Parse(prompt[(prompt.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            order.Add(id);
            await Task.Yield();
            return "translation " + id;
        }, progress);
        CollectionAssert.AreEqual(new[] { 2, 4, 0, 1, 3 }, order);
        CollectionAssert.AreEqual(order, updates.Select(x => x.LineId).ToArray());
        Assert.HasCount(5, updates);
        Assert.AreEqual("translation 2", updates[0].Document.Lines[2].Secondary);
        Assert.IsNull(updates[0].Document.Lines[0].Secondary);
        for (var id = 0; id < source.Lines.Count; id++)
        {
            Assert.AreEqual(source.Lines[id].Text, result.Document.Lines[id].Text);
            Assert.AreEqual(source.Lines[id].Start, result.Document.Lines[id].Start);
            Assert.AreEqual(source.Lines[id].End, result.Document.Lines[id].End);
            Assert.AreSame(source.Lines[id].Words, result.Document.Lines[id].Words);
            Assert.AreEqual("translation " + id, result.Document.Lines[id].Secondary);
        }
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.IsTrue(updates.All(update => !update.IsCurrent), "Queued partial events retire when the complete result returns.");
        var saved = await fixture.Cache.ReadAsync(fixture.Key, default);
        Assert.IsNotNull(saved);
        using var json = JsonDocument.Parse(saved);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, json.RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray());
        var cached = await fixture.Translate((_, _) => throw new AssertFailedException("Complete cache should avoid inference."), progress);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, cached.Outcome);
        Assert.HasCount(5, updates, "Cache hits publish a final document, not simulated partial events.");
    }

    [TestMethod]
    public async Task LaterFailureRetiresEarlierEphemeralEventsAndNeverCaches()
    {
        using var fixture = new Fixture();
        var updates = new List<LyricsTranslationProgress>();
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { updates.Add(update); return Task.CompletedTask; });
        var calls = 0;
        var result = await fixture.Translate((_, _) => Task.FromResult(++calls == 1 ? "translation" : "invalid\nline"), progress);
        Assert.AreEqual(LyricsTranslationOutcome.Failed, result.Outcome);
        Assert.AreSame(fixture.Source, result.Document);
        Assert.HasCount(1, updates);
        Assert.IsFalse(updates[0].IsCurrent, "Late dispatcher execution must not restore a rejected partial song.");
        Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key, default));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidatedRequestOrCacheClearStopsAllLaterProgressAndCache(bool clearCache)
    {
        using var fixture = new Fixture();
        var current = true;
        var updates = new List<LyricsTranslationProgress>();
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => current, async (update, _) =>
        {
            updates.Add(update);
            if (clearCache) await fixture.Cache.ClearAsync(default); else current = false;
            Assert.IsFalse(update.IsCurrent);
        });
        var calls = 0;
        var result = await fixture.Translate((_, _) => { calls++; return Task.FromResult("translated"); }, progress);
        Assert.AreSame(fixture.Source, result.Document);
        Assert.AreEqual(1, calls);
        Assert.HasCount(1, updates);
        Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key, default));
    }

    [TestMethod]
    public async Task CancellationAfterPartialRetiresSnapshotAndPreservesRetry()
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        LyricsTranslationProgress? partial = null;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { partial = update; stop.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Translate((_, _) => Task.FromResult("translated"), progress, stop.Token));
        Assert.IsNotNull(partial); Assert.IsFalse(partial.IsCurrent);
        Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key, default));
        Assert.AreEqual(LyricsTranslationOutcome.Translated,
            (await fixture.Translate((_, _) => Task.FromResult("retry translated"), null)).Outcome);
    }

    [TestMethod]
    public async Task MatchingProviderTranslationBypassesEveryPartialCallback()
    {
        using var fixture = new Fixture();
        var provider = fixture.Source with { Lines = fixture.Source.Lines.Select(line => line with
            { Secondary = "provider", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "en" }).ToArray() };
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => throw new AssertFailedException("Provider translations must win."));
        var result = await fixture.Coordinator.TranslateAsync(Fixture.Query, provider, "en", Fixture.Identity, fixture.Cache.Generation,
            (_, _) => throw new AssertFailedException("No inference should run."), default, progress);
        Assert.AreSame(provider, result.Document);
    }

    [TestMethod]
    public async Task CacheCommitChecksRequestAgainAfterAsyncWork()
    {
        using var fixture = new Fixture();
        var checks = 0;
        await fixture.Cache.WriteAsync(fixture.Key, "[{\"id\":0,\"text\":\"translated\"}]", fixture.Cache.Generation, default,
            () => ++checks == 1);
        Assert.IsTrue(checks >= 2);
        Assert.IsNull(await fixture.Cache.ReadAsync(fixture.Key, default));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-progress-" + Guid.NewGuid().ToString("N"));
        internal static readonly LyricsQuery Query = new("song", "artist", "album", TimeSpan.FromSeconds(5), "song-identity");
        internal static readonly string Identity = PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64));
        internal LyricsDocument Source { get; } = new(Enumerable.Range(0, 5).Select(id => new LyricsLine(
            TimeSpan.FromSeconds(id), TimeSpan.FromSeconds(id + 1), "source " + id, null,
            [new("word " + id, TimeSpan.FromSeconds(id), TimeSpan.FromSeconds(id + 1))])).ToArray(), LyricsProviderKind.LocalLrc);
        internal AiLyricsCache Cache { get; }
        internal PlainHyLyricsCoordinator Coordinator { get; }
        internal string Key => PlainHyLyricsProtocol.CacheKey(Query, Source, "en", Identity);
        internal Fixture() { Cache = new(_root); Coordinator = new(Cache); }
        internal Task<LyricsTranslationResult> Translate(Func<string, CancellationToken, Task<string>> infer,
            LyricsTranslationProgressContext? progress, CancellationToken token = default) =>
            Coordinator.TranslateAsync(Query, Source, "en", Identity, Cache.Generation, infer, token, progress);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
