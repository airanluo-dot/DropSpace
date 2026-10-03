using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ChineseLyricsTranslationTests
{
    private static readonly LyricsQuery Query = new("合成曲 (with SingerB)", "SingerA、SingerB", "合成专辑", TimeSpan.FromSeconds(40));
    private static readonly string Identity = PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChineseSongBypassesInferenceProgressAndOldRewriteCache(bool timed)
    {
        using var fixture = new Fixture();
        var source = ChineseSource(timed);
        var oldIds = timed ? new[] { 0, 3, 7 } : new[] { 0 };
        var oldKey = OldKey(source, oldIds);
        var poison = JsonSerializer.Serialize(oldIds.Select(id => new { id, text = "合成中文改写，" }));
        await fixture.Cache.WriteAsync(oldKey, poison, default);
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default));
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => throw new AssertFailedException("No AI progress for an admitted same-language song."));
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (_, _) => throw new AssertFailedException("Same-language lyrics must not reach inference."), default, progress);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
        CollectionAssert.AreEqual(source.Lines.ToArray(), result.Document.Lines.ToArray());
        Assert.IsTrue(result.Document.Lines.All(line => LyricsDisplayPolicy.SecondaryPresentation(line, "zh-CN", true) is null));
        Assert.AreEqual(poison, await fixture.Cache.ReadAsync(oldKey, default), "Invalidation must not clear the shared cache.");
        Assert.IsNull(await fixture.Cache.ReadAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity), default));
    }

    [TestMethod]
    public async Task StaleAiSecondariesAreRemovedWithoutChangingOriginalsOrProviderTranslation()
    {
        using var fixture = new Fixture();
        var original = ChineseSource(true);
        var source = original with { Lines = original.Lines.Select(line => line with
            { Secondary = "合成中文改写", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "zh-CN" }).ToArray() };
        source = source with { Lines = [.. source.Lines.Take(4), source.Lines[4] with
            { Secondary = "Provider original", TranslationOrigin = LyricsTranslationOrigin.Provider,
                TranslationLanguage = "en-US", TranslationLanguageIsExplicit = true }, .. source.Lines.Skip(5)] };
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (_, _) => throw new AssertFailedException("No inference while removing stale same-language AI rows."), default);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
        Assert.AreEqual(source.Lines[4], result.Document.Lines[4]);
        for (var id = 0; id < source.Lines.Count; id++)
        {
            Assert.AreEqual(original.Lines[id].Text, result.Document.Lines[id].Text);
            Assert.AreEqual(original.Lines[id].Start, result.Document.Lines[id].Start);
            Assert.AreEqual(original.Lines[id].End, result.Document.Lines[id].End);
            Assert.AreSame(original.Lines[id].Words, result.Document.Lines[id].Words);
            if (id != 4) Assert.IsNull(LyricsDisplayPolicy.SecondaryPresentation(result.Document.Lines[id], "zh-CN", true));
        }
    }

    [TestMethod]
    [DataRow("I will watch the boats with you")]
    [DataRow("川の水が揺れる")]
    [DataRow("작은 배를 기다려요")]
    public async Task ForeignVerseSurvivesNewAdmissionAndRejectsPreviousPolicyCache(string foreign)
    {
        using var fixture = new Fixture();
        var chinese = ChineseSource(true);
        var source = chinese with { Lines = [.. chinese.Lines.Take(7), chinese.Lines[7] with { Text = foreign }] };
        var oldIds = new[] { 0, 3, 7 };
        var poison = JsonSerializer.Serialize(oldIds.Select(id => new { id, text = "旧策略改写" }));
        var oldKey = OldKey(source, oldIds);
        await fixture.Cache.WriteAsync(oldKey, poison, default);
        Assert.AreNotEqual(oldKey, PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity));
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default));
        // Even data under the new key must obey the current admitted original IDs.
        await fixture.Cache.WriteAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity),
            "[{\"id\":5,\"text\":\"伪造中文改写\"}]", default);
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default));
        var calls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true, (update, _) =>
        {
            Assert.AreEqual(7, update.LineId);
            Assert.AreEqual(1, update.TotalLineCount);
            Assert.IsTrue(update.Document.Lines.Take(7).All(line => line.Secondary is null));
            return Task.CompletedTask;
        });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (prompt, _) =>
            {
                calls++;
                Assert.AreEqual(PlainHyLyricsProtocol.BuildPrompt(foreign, "zh-CN"), prompt);
                return Task.FromResult("我在木桥旁等你");
            }, default, progress);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        CollectionAssert.AreEqual(source.Lines.Take(7).ToArray(), result.Document.Lines.Take(7).ToArray());
        Assert.AreEqual(LyricsTranslationOrigin.LocalAi, result.Document.Lines[7].TranslationOrigin);
        var cached = await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default);
        Assert.IsNotNull(cached);
        CollectionAssert.AreEqual(result.Document.Lines.ToArray(), cached.Document.Lines.ToArray());
        Assert.AreEqual(poison, await fixture.Cache.ReadAsync(oldKey, default));
    }

    [TestMethod]
    public async Task JapaneseHanRemainsAdmittedBesideChineseAndRejectsVersion6PartialCoverage()
    {
        using var fixture = new Fixture();
        string[] text = ["我会把木箱搬给你", "圧倒的存在", "你们守在小桥旁", "川の水が揺れる"];
        var source = new LyricsDocument(text.Select((part, id) => new LyricsLine(TimeSpan.FromSeconds(id * 3),
            TimeSpan.FromSeconds(id * 3 + 3), part, null, [])).ToArray(), LyricsProviderKind.NetEase)
            with { Match = new(Query.Title, Query.Artist, Query.Album, 40, 12) };
        await fixture.Cache.WriteAsync(OldKey(source, [3], "chinese-document-eligibility-v6"),
            "[{\"id\":3,\"text\":\"旧的假名译文\"}]", default);
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default));
        var prompts = new List<string>();
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (prompt, _) => { prompts.Add(prompt); return Task.FromResult("合成外语译文" + prompts.Count); }, default);
        CollectionAssert.AreEqual(new[] { PlainHyLyricsProtocol.BuildPrompt(text[1], "zh-CN"),
            PlainHyLyricsProtocol.BuildPrompt(text[3], "zh-CN") }, prompts);
        Assert.AreEqual(source.Lines[0], result.Document.Lines[0]);
        Assert.AreEqual(source.Lines[2], result.Document.Lines[2]);
        foreach (var id in new[] { 1, 3 }) Assert.AreEqual(LyricsTranslationOrigin.LocalAi, result.Document.Lines[id].TranslationOrigin);
        var cached = await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default);
        Assert.IsNotNull(cached);
        CollectionAssert.AreEqual(result.Document.Lines.ToArray(), cached.Document.Lines.ToArray());
    }

    [TestMethod]
    public async Task Version6CacheMissesEvenWhenAdmittedOriginalIdsHaveNotChanged()
    {
        using var fixture = new Fixture();
        var chinese = ChineseSource(true);
        var source = chinese with { Lines = [.. chinese.Lines.Take(7), chinese.Lines[7] with { Text = "I will watch the boats with you" }] };
        CollectionAssert.AreEqual(new[] { 7 }, LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
        var oldKey = OldKey(source, [7], "chinese-document-eligibility-v6");
        await fixture.Cache.WriteAsync(oldKey, "[{\"id\":7,\"text\":\"旧策略译文\"}]", default);
        Assert.AreNotEqual(oldKey, PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity));
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default));
    }

    [TestMethod]
    public async Task UntimedMixedRowRejectsVersion6ChineseRewriteAndTranslatesOnlyForeignSegments()
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("我们带着蓝色雨伞\n圧倒的存在\n我的小船停在岸边\n川の水が揺れる", LyricsProviderKind.NetEase)
            with { Match = new(Query.Title, Query.Artist, Query.Album, 40, 12) };
        await fixture.Cache.WriteAsync(OldKey(source, [0], "chinese-document-eligibility-v6"),
            "[{\"id\":0,\"text\":\"旧的中文自我改写\"}]", default);
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default));
        var prompts = new List<string>();
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (prompt, _) => { prompts.Add(prompt); return Task.FromResult("合成外语译文" + prompts.Count); }, default);
        CollectionAssert.AreEqual(new[] { PlainHyLyricsProtocol.BuildPrompt("圧倒的存在", "zh-CN"),
            PlainHyLyricsProtocol.BuildPrompt("川の水が揺れる", "zh-CN") }, prompts);
        Assert.AreEqual(source.Lines[0].Text, result.Document.Lines[0].Text);
        Assert.AreEqual("合成外语译文1 合成外语译文2", result.Document.Lines[0].Secondary);
        var provider = source with { Lines = [source.Lines[0] with { Secondary = "提供方完整译文", TranslationOrigin = LyricsTranslationOrigin.Provider }] };
        Assert.AreSame(provider, LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(provider, "zh-CN"));
    }

    [TestMethod]
    public async Task HeaderSubstringsAndUnprovenColonWordsReachInferenceWithOriginalText()
    {
        using var fixture = new Fixture();
        string[] text = ["Yes - I will love you", "No:", "Why:"];
        var source = new LyricsDocument(text.Select((part, id) => new LyricsLine(TimeSpan.FromSeconds(id * 3),
            TimeSpan.FromSeconds(id * 3 + 3), part, null, [])).ToArray(), LyricsProviderKind.NetEase)
            with { Match = new("Love", "Yes", "合成专辑", 40, 12) };
        var prompts = new List<string>();
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (prompt, _) => { prompts.Add(prompt); return Task.FromResult("合成外语译文" + prompts.Count); }, default);
        CollectionAssert.AreEqual(text.Select(part => PlainHyLyricsProtocol.BuildPrompt(part, "zh-CN")).ToArray(), prompts);
        for (var id = 0; id < source.Lines.Count; id++) Assert.AreEqual(text[id], result.Document.Lines[id].Text);
    }

    private static string OldKey(LyricsDocument source, int[] ids, string policy = "lexical-context-eligibility-v5") => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        cache = "plain-hy-complete-song-v2", eligibility = policy,
        sourceLanguages = source.Lines.Select(line => line.SourceLanguage), eligibleIds = ids,
        protocol = PlainHyLyricsProtocol.Version, documentKey = LyricsTranslationPrompt.CacheKey(Query, source, "zh-CN", Identity),
    })));

    private static LyricsDocument ChineseSource(bool timed)
    {
        string[] text = ["SingerA、SingerB - 合成曲 (with SingerB)", "作词：WriterX", "作曲：ComposerY/ComposerZ",
            "SingerA:", "我们带着蓝色雨伞", "山谷的纸船", "我的小船停在岸边", "晴"];
        var source = timed ? new LyricsDocument(text.Select((part, id) => new LyricsLine(TimeSpan.FromSeconds(id * 3),
            TimeSpan.FromSeconds(id * 3 + 3), part, null, [])).ToArray(), LyricsProviderKind.NetEase)
            : LyricsParser.Parse(string.Join("\n", text), LyricsProviderKind.NetEase);
        return source with { Match = new(Query.Title, Query.Artist, Query.Album, 40, 12) };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-ChineseAdmission-" + Guid.NewGuid().ToString("N"));
        public AiLyricsCache Cache { get; }
        public PlainHyLyricsCoordinator Coordinator { get; }
        public Fixture() { Cache = new(_root); Coordinator = new(Cache); }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
