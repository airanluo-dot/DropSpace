using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AuditLyricsAdmissionTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(60));
    private static string Identity => PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64));

    [TestMethod]
    [DataRow("Your blue cup waits beside the window.", false)]
    [DataRow("Your blue cup waits beside the window.", true)]
    [DataRow("You said the northern road was closed.", false)]
    [DataRow("You said the northern road was closed.", true)]
    public async Task RecognizedEnglishBypassesPoisonedCacheInferenceAndProgress(string text, bool providerTranslation)
    {
        using var fixture = new Fixture();
        var source = providerTranslation
            ? LyricsParser.Parse("[00:01]你的蓝色杯子放在窗边", LyricsProviderKind.NetEase, "[00:01]" + text)
            : LyricsParser.Parse("[00:01]" + text, LyricsProviderKind.NetEase);
        var key = PlainHyLyricsProtocol.CacheKey(Query, source, "en-US", Identity);
        await fixture.Cache.WriteAsync(key, "[{\"id\":0,\"text\":\"Unwanted AI rewrite\"}]", default);
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "en-US", Identity, default));
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => throw new AssertFailedException("No progress should be published on language bypass."));
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "en-US", Identity, fixture.Cache.Generation,
            (_, _) => throw new AssertFailedException("No inference should run on language bypass."), default, progress);
        CollectionAssert.AreEqual(source.Lines.ToArray(), result.Document.Lines.ToArray());
        Assert.AreEqual(providerTranslation ? LyricsTranslationOrigin.Provider : LyricsTranslationOrigin.None,
            result.Document.Lines[0].TranslationOrigin);
    }

    [TestMethod]
    [DataRow("作词：某人\nI love you\n作曲：另一人\n君の声が聞こえる", "zh-CN", "I love you", "君の声が聞こえる")]
    [DataRow("I love you\n作曲：另一人\nkimi no na wa", "en-US", "kimi no na wa", null)]
    [DataRow("作词：某人\n我的世界充满阳光\nI need you\n作曲：另一人\n愛", "zh-CN", "I need you", "愛")]
    public async Task UntimedPhysicalSegmentsUseFixedPromptsAndOneStableDisplayIdAcrossProgressCacheAndFinal(
        string text, string target, string first, string? second)
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse(text, LyricsProviderKind.NetEase);
        Assert.HasCount(1, source.Lines);
        var key = PlainHyLyricsProtocol.CacheKey(Query, source, target, Identity);
        var segments = second is null ? new[] { first } : new[] { first, second };
        var expectedPrompts = segments.Select(segment => PlainHyLyricsProtocol.BuildPrompt(segment, target)).ToArray();
        var prompts = new List<string>();
        var updates = new List<LyricsTranslationProgress>();
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.FromSeconds(20), () => true, async (update, _) =>
        {
            Assert.AreEqual(0, update.LineId);
            Assert.AreEqual(1, update.TotalLineCount, "Model segments must not invent display rows or IDs.");
            Assert.AreEqual(segments.Length, prompts.Count, "No partial physical block may be published as a complete display row.");
            Assert.IsNull(await fixture.Cache.ReadAsync(key, default));
            AssertOriginalDisplayRow(source.Lines[0], update.Document.Lines[0]);
            updates.Add(update);
        });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, target, Identity, fixture.Cache.Generation,
            (prompt, _) => { prompts.Add(prompt); return Task.FromResult("translated segment " + prompts.Count); }, default, progress);
        CollectionAssert.AreEqual(expectedPrompts, prompts);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.HasCount(1, updates);
        Assert.HasCount(1, result.Document.Lines);
        AssertOriginalDisplayRow(source.Lines[0], result.Document.Lines[0]);
        var expectedTranslation = string.Join(" ", Enumerable.Range(1, segments.Length).Select(i => "translated segment " + i));
        Assert.AreEqual(expectedTranslation, result.Document.Lines[0].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.LocalAi, result.Document.Lines[0].TranslationOrigin);
        var saved = await fixture.Cache.ReadAsync(key, default);
        Assert.IsNotNull(saved);
        using var json = JsonDocument.Parse(saved);
        Assert.AreEqual(1, json.RootElement.GetArrayLength());
        Assert.AreEqual(0, json.RootElement[0].GetProperty("id").GetInt32());
        Assert.AreEqual(expectedTranslation, json.RootElement[0].GetProperty("text").GetString());
        var cached = await fixture.Coordinator.TranslateAsync(Query, source, target, Identity, fixture.Cache.Generation,
            (_, _) => throw new AssertFailedException("The complete block should be read from cache."), default, progress);
        Assert.HasCount(1, updates, "A cache hit must not replay progressive events.");
        CollectionAssert.AreEqual(result.Document.Lines.ToArray(), cached.Document.Lines.ToArray());
    }

    [TestMethod]
    public async Task MultipleDisplayRowsRetainTheirOriginalIdsWhenOneContainsCreditsAndSeveralPhysicalSegments()
    {
        using var fixture = new Fixture();
        var block = LyricsParser.Parse("作词：某人\nI love you\n作曲：另一人\n君の声が聞こえる", LyricsProviderKind.NetEase).Lines.Single();
        var source = new LyricsDocument([
            new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "作词：Someone", null, []),
            block with { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(8) },
            new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), "我的世界充满阳光", null, []),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), "kimi no na wa", null, []),
        ], LyricsProviderKind.NetEase);
        var updates = new List<LyricsTranslationProgress>();
        var prompts = new List<string>();
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.FromSeconds(11), () => true,
            (update, _) => { updates.Add(update); return Task.CompletedTask; });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (prompt, _) => { prompts.Add(prompt); return Task.FromResult("translated " + prompts.Count); }, default, progress);
        CollectionAssert.AreEqual(new[] { 3, 1 }, updates.Select(update => update.LineId).ToArray());
        Assert.IsTrue(updates.All(update => update.TotalLineCount == 2));
        CollectionAssert.AreEqual(new[] { "kimi no na wa", "I love you", "君の声が聞こえる" }
            .Select(segment => PlainHyLyricsProtocol.BuildPrompt(segment, "zh-CN")).ToArray(), prompts);
        for (var i = 0; i < source.Lines.Count; i++) AssertOriginalDisplayRow(source.Lines[i], result.Document.Lines[i]);
        Assert.AreEqual(source.Lines[0], result.Document.Lines[0]);
        Assert.AreEqual(source.Lines[2], result.Document.Lines[2]);
        var saved = await fixture.Cache.ReadAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity), default);
        Assert.IsNotNull(saved);
        using var json = JsonDocument.Parse(saved);
        CollectionAssert.AreEqual(new[] { 1, 3 }, json.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CopiedPhysicalSegmentsRemainNeutralDespiteCreditsInOriginalDisplayText(bool surroundingWhitespace)
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("作词：某人\nkimi no na wa\n作曲：另一人\n愛", LyricsProviderKind.NetEase);
        var segments = new[] { "kimi no na wa", "愛" };
        var inferenceCalls = 0;
        var progressCalls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true, (update, _) =>
        {
            progressCalls++;
            Assert.IsNull(update.Document.Lines[0].Secondary);
            Assert.AreEqual(LyricsTranslationOrigin.None, update.Document.Lines[0].TranslationOrigin);
            return Task.CompletedTask;
        });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (prompt, _) =>
            {
                var segment = segments[inferenceCalls++];
                Assert.AreEqual(PlainHyLyricsProtocol.BuildPrompt(segment, "zh-CN"), prompt);
                return Task.FromResult(surroundingWhitespace ? " " + segment + " " : segment);
            }, default, progress);
        Assert.AreEqual(2, inferenceCalls);
        Assert.AreEqual(1, progressCalls);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
        CollectionAssert.AreEqual(source.Lines.ToArray(), result.Document.Lines.ToArray());
        Assert.IsNull(await fixture.Cache.ReadAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity), default));
        var again = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (_, _) => throw new AssertFailedException("The neutral result should suppress a repeated inference attempt."), default);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, again.Outcome);
        var key = PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity);
        await fixture.Cache.WriteAsync(key, JsonSerializer.Serialize(new[] { new { id = 0, text = string.Join(" ", segments) } }), default);
        var coldCoordinator = new PlainHyLyricsCoordinator(fixture.Cache);
        var cachedCopy = await coldCoordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default);
        Assert.IsNotNull(cachedCopy);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, cachedCopy.Outcome);
        CollectionAssert.AreEqual(source.Lines.ToArray(), cachedCopy.Document.Lines.ToArray());
    }

    [TestMethod]
    public async Task InvalidPhysicalSegmentCannotPublishOrCacheAPartiallyTranslatedDisplayBlock()
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("作词：某人\nI love you\n作曲：另一人\n君の声が聞こえる", LyricsProviderKind.NetEase);
        var calls = 0;
        var progressCalls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => { progressCalls++; return Task.CompletedTask; });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (_, _) => Task.FromResult(++calls == 1 ? "First valid translation" : "extra\ninvalid output"), default, progress);
        Assert.AreEqual(LyricsTranslationOutcome.Failed, result.Outcome);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(0, progressCalls);
        CollectionAssert.AreEqual(source.Lines.ToArray(), result.Document.Lines.ToArray());
        Assert.IsNull(await fixture.Cache.ReadAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity), default));
    }

    private static void AssertOriginalDisplayRow(LyricsLine expected, LyricsLine actual)
    {
        Assert.AreEqual(expected.Text, actual.Text);
        Assert.AreEqual(expected.Start, actual.Start);
        Assert.AreEqual(expected.End, actual.End);
        Assert.AreSame(expected.Words, actual.Words);
        Assert.AreEqual(expected.SourceLanguage, actual.SourceLanguage);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-audit-admission-" + Guid.NewGuid().ToString("N"));
        public AiLyricsCache Cache { get; }
        public PlainHyLyricsCoordinator Coordinator { get; }
        public Fixture() { Cache = new(_root); Coordinator = new(Cache); }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
