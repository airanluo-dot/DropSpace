using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta16WholeTrackPipelineTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(9), "beta16-track")
        { PreferredTranslationLanguage = "zh" };
    private static readonly string Identity = PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64));
    private static LyricsDocument Source(string? native = null) => new LyricsDocument([
        new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "I carry the sunlight across the open water.", native, [])
        { TranslationOrigin = native is null ? LyricsTranslationOrigin.None : LyricsTranslationOrigin.Provider,
            TranslationLanguage = native is null ? null : "zh-Hant", TranslationLanguageIsExplicit = native is null ? null : true },
        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), "I carry the sunlight across the open water.", null, []),
    ], LyricsProviderKind.NetEase).Bind(Query, Query.Title, Query.Artist, Query.Album, 9, 13, "recording");
    private static LyricsDocument Prepare(LyricsDocument source, string target = "zh") =>
        LyricsLanguagePolicy.Prepare(source, target, new("__label__en", 0.99, 0.001));

    [TestMethod]
    public async Task TargetOriginalAndPartialNativeStopSupplementalLookupAndAllAiCacheInference()
    {
        using var fixture = new CacheFixture();
        foreach (var targetOriginal in new[] { false, true })
        {
            var raw = Source(targetOriginal ? null : "我會陪你走到天亮，Yeah Frank Ocean");
            var query = Query with { PreferredTranslationLanguage = targetOriginal ? "en" : "zh" };
            var primary = new Provider(LyricsProviderKind.NetEase, raw);
            var backup = new Provider(LyricsProviderKind.QqMusic, raw with { Provider = LyricsProviderKind.QqMusic });
            var result = await new LyricsService(new([primary, backup]), languageIdentifier: new Identifier())
                .QueryDetailedAsync(query, new() { Enabled = true, BackupProvider = LyricsProviderKind.QqMusic,
                    SearchRemainingProviders = false }, default);
            Assert.AreEqual(0, backup.Calls);
            Assert.IsFalse(LyricsLanguagePolicy.CanTranslate(result.Document, query.PreferredTranslationLanguage!));
            Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(query, result.Document,
                query.PreferredTranslationLanguage!, Identity, default));
            var calls = 0;
            var translated = await fixture.Coordinator.TranslateAsync(query, result.Document,
                query.PreferredTranslationLanguage!, Identity, fixture.Cache.Generation,
                (_, _) => { calls++; return Task.FromResult("意外翻譯"); }, default);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(raw.Lines[0].Secondary, translated.Document.Lines[0].Secondary);
            Assert.IsNull(translated.Document.Lines[1].Secondary);
        }
    }

    [TestMethod]
    public async Task UnknownAfterBoundedNoResultUsesExistingProgressiveProtocolAndRepeatedOccurrences()
    {
        using var fixture = new CacheFixture();
        var raw = Source();
        var primary = new Provider(LyricsProviderKind.NetEase, raw);
        var backup = new Provider(LyricsProviderKind.QqMusic, LyricsDocument.Empty);
        var queried = await new LyricsService(new([primary, backup]), languageIdentifier: new Identifier())
            .QueryDetailedAsync(Query, new() { Enabled = true, BackupProvider = LyricsProviderKind.QqMusic,
                SearchRemainingProviders = false }, default);
        Assert.AreEqual(1, backup.Calls);
        Assert.IsTrue(LyricsLanguagePolicy.CanTranslate(queried.Document, "zh"));
        var calls = 0;
        var published = new List<int>();
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { published.Add(update.LineId); return Task.CompletedTask; });
        var result = await fixture.Coordinator.TranslateAsync(Query, queried.Document, "zh", Identity,
            fixture.Cache.Generation, (prompt, _) =>
            {
                Assert.AreEqual(PlainHyLyricsProtocol.BuildPrompt(raw.Lines[0].Text, "zh"), prompt);
                return Task.FromResult(++calls == 1 ? "我带着阳光走过开阔的水面。" : "我携着阳光走过开阔的水面。");
            }, default, progress);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        CollectionAssert.AreEqual(new[] { 0, 1 }, published.ToArray());
        Assert.AreEqual(1, calls, "Repeated source text may reuse the request-local segment result.");
        Assert.IsTrue(result.Document.Lines.All(line => line.TranslationOrigin == LyricsTranslationOrigin.LocalAi));
        Assert.AreNotEqual(LyricsTranslationOutput.LineIdentity(result.Document, 0), LyricsTranslationOutput.LineIdentity(result.Document, 1));
        Assert.IsNotNull(await fixture.Coordinator.TryGetCachedAsync(Query, queried.Document, "zh", Identity, default));
    }

    [TestMethod]
    public async Task LateNativeVetoRetiresProgressCacheAndEntireResult()
    {
        using var fixture = new CacheFixture();
        var source = Prepare(Source());
        var isCurrent = true;
        var calls = 0;
        var native = Prepare(Source("我会陪你走到天亮，Yeah"));
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => isCurrent,
            (_, _) => { isCurrent = false; return Task.CompletedTask; });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh", Identity,
            fixture.Cache.Generation, (_, _) => { calls++; return Task.FromResult("已退休的AI输出"); }, default, progress);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
        Assert.IsNull(await fixture.Cache.ReadAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "zh", Identity), default));
        var restored = LyricsTranslationOutput.Reconcile(native, source, result.Document, "zh");
        Assert.AreEqual(native.Lines[0].Secondary, restored.Lines[0].Secondary);
        Assert.IsNull(restored.Lines[1].Secondary);
        Assert.IsFalse(LyricsTranslationOutput.TryApply("[{\"id\":1,\"text\":\"旧缓存结果\"}]", native, [1], "zh", out _));
    }

    [TestMethod]
    public async Task SourceRevisionsAndTargetBindCacheWhileLegacyAiDoesNotBypassVeto()
    {
        using var fixture = new CacheFixture();
        var source = Prepare(Source());
        var key = PlainHyLyricsProtocol.CacheKey(Query, source, "zh", Identity);
        await fixture.Cache.WriteAsync(key, "[{\"id\":0,\"text\":\"译文一\"},{\"id\":1,\"text\":\"译文二\"}]", default);
        var native = Prepare(Source("原生译文保留Yeah"));
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, native, "zh", Identity, default));
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "en", Identity, default));
        var changed = source with { Lines = [source.Lines[0], source.Lines[1] with { Text = "A different source revision" }] };
        Assert.IsFalse(LyricsLanguagePolicy.CanTranslate(changed, "zh"));
        Assert.AreNotEqual(key, PlainHyLyricsProtocol.CacheKey(Query, Prepare(changed), "zh", Identity));
    }

    [TestMethod]
    public async Task NonTargetProviderRoleSurvivesAiOutputRevisionAndCanBeRestored()
    {
        using var fixture = new CacheFixture();
        var raw = Source("君の声が聞こえる") with { Lines = [Source("君の声が聞こえる").Lines[0] with
            { TranslationLanguage = "ja", TranslationLanguageIsExplicit = true }, Source().Lines[1]] };
        var source = Prepare(raw);
        Assert.IsTrue(LyricsLanguagePolicy.CanTranslate(source, "zh"));
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh", Identity,
            fixture.Cache.Generation, (_, _) => Task.FromResult("我带着阳光走过开阔的水面。"), default);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.IsTrue(LyricsLanguagePolicy.CanTranslate(result.Document, "zh"));
        Assert.AreEqual(source.TranslationAdmission!.TranslationRevision,
            LyricsLanguagePolicy.TranslationRevision(result.Document));
        Assert.AreEqual(PlainHyLyricsProtocol.CacheKey(Query, source, "zh", Identity),
            PlainHyLyricsProtocol.CacheKey(Query, result.Document, "zh", Identity));
        var restored = LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(result.Document, "en");
        Assert.AreEqual(raw.Lines[0].Secondary, restored.Lines[0].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, restored.Lines[0].TranslationOrigin);
        Assert.IsNull(restored.Lines[1].Secondary);
    }

    private sealed class Identifier : ILyricsLanguageIdentifier
    {
        public Task<LyricsDocument> PrepareAsync(LyricsDocument document, string targetLanguage, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Prepare(document, targetLanguage)); }
    }
    private sealed class Provider(LyricsProviderKind kind, LyricsDocument document) : ILyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public int Calls { get; private set; }
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(document); }
    }
    private sealed class CacheFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-beta16-" + Guid.NewGuid().ToString("N"));
        public AiLyricsCache Cache { get; }
        public PlainHyLyricsCoordinator Coordinator { get; }
        public CacheFixture() { Cache = new(_root); Coordinator = new(Cache); }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
