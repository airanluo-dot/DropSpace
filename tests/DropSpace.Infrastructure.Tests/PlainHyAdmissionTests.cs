using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PlainHyAdmissionTests
{
    private const string RuntimeHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly LyricsQuery Query = new("song", "artist", "album", TimeSpan.FromSeconds(40));
    private static string Identity => PlainHyLyricsProtocol.InferenceIdentity(RuntimeHash);

    [TestMethod]
    public async Task MixedSongUsesIdenticalOriginalIdsForInferenceProgressFinalAndCache()
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("[00:00]作词：Someone\n[00:01]我的世界充满阳光\n[00:04]I will stay with you\n[00:07]我们仍在这里\n[00:10]君の声が聞こえる\n[00:13]作曲：Someone", LyricsProviderKind.NetEase);
        var ids = new List<int>();
        var prompts = new List<string>();
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.FromSeconds(10), () => true,
            (update, _) =>
            {
                Assert.AreEqual(2, update.TotalLineCount);
                Assert.IsNull(update.Document.Lines[0].Secondary);
                Assert.IsNull(update.Document.Lines[1].Secondary);
                return Task.CompletedTask;
            });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (prompt, _) => { prompts.Add(prompt); return Task.FromResult(prompt.Contains("君の", StringComparison.Ordinal) ? "我听见你的声音" : "我会陪在你的身边"); }, default, progress);
        CollectionAssert.AreEqual(new[] { PlainHyLyricsProtocol.BuildPrompt(source.Lines[4].Text, "zh-CN"), PlainHyLyricsProtocol.BuildPrompt(source.Lines[2].Text, "zh-CN") }, prompts);
        foreach (var i in new[] { 0, 1, 3, 5 }) Assert.AreEqual(source.Lines[i], result.Document.Lines[i]);
        foreach (var i in new[] { 2, 4 }) Assert.AreEqual(LyricsTranslationOrigin.LocalAi, result.Document.Lines[i].TranslationOrigin);
        var saved = await fixture.Cache.ReadAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity), default);
        Assert.IsNotNull(saved);
        using (var json = JsonDocument.Parse(saved)) ids.AddRange(json.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        CollectionAssert.AreEqual(new[] { 2, 4 }, ids);
        var cached = await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default);
        Assert.IsNotNull(cached);
        CollectionAssert.AreEqual(result.Document.Lines.ToArray(), cached.Document.Lines.ToArray());
    }

    [TestMethod]
    public async Task OldAiCacheAndWrongIdCacheCannotReintroduceSameLanguageOrCredits()
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("[00:00]作词：Someone\n[00:01]我的世界充满阳光\n[00:04]I will stay with you", LyricsProviderKind.NetEase);
        var documentKey = LyricsTranslationPrompt.CacheKey(Query, source, "zh-CN", Identity);
        var legacyKey = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { cache = "plain-hy-complete-song-v1", protocol = PlainHyLyricsProtocol.Version, documentKey })));
        const string poison = "[{\"id\":0,\"text\":\"改写制作信息\"},{\"id\":1,\"text\":\"改写中文原句\"},{\"id\":2,\"text\":\"旧译文\"}]";
        await fixture.Cache.WriteAsync(legacyKey, poison, default);
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default));
        await fixture.Cache.WriteAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity), poison, default);
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "zh-CN", Identity, default));
        var calls = 0;
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "zh-CN", Identity, fixture.Cache.Generation,
            (_, _) => { calls++; return Task.FromResult("我会陪在你的身边"); }, default);
        Assert.AreEqual(1, calls);
        Assert.IsNull(result.Document.Lines[0].Secondary);
        Assert.IsNull(result.Document.Lines[1].Secondary);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MixedClauseAdmissionKeepsOriginalIdsAcrossProgressFinalAndVersionedCache(bool mixedProvider)
    {
        using var fixture = new Fixture();
        const string mixed = "I love you, wo hen xiang ni";
        var source = LyricsParser.Parse("[00:00]作词：Someone\n[00:01]" + (mixedProvider ? "君が好き" : mixed) +
            "\n[00:04]我的世界充满阳光", LyricsProviderKind.NetEase, mixedProvider ? "[00:01]" + mixed : null);
        var oldKey = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            cache = "plain-hy-complete-song-v2", eligibility = "lexical-context-eligibility-v4",
            sourceLanguages = source.Lines.Select(line => line.SourceLanguage),
            eligibleIds = mixedProvider ? new[] { 1, 2 } : new[] { 2 }, protocol = PlainHyLyricsProtocol.Version,
            documentKey = LyricsTranslationPrompt.CacheKey(Query, source, "en-US", Identity),
        })));
        await fixture.Cache.WriteAsync(oldKey, "[{\"id\":2,\"text\":\"POISON old admission\"}]", default);
        Assert.IsNull(await fixture.Coordinator.TryGetCachedAsync(Query, source, "en-US", Identity, default));
        var prompts = new List<string>();
        var progressCalls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true, (update, _) =>
        {
            progressCalls++;
            Assert.AreEqual(2, update.TotalLineCount);
            Assert.IsNull(update.Document.Lines[0].Secondary);
            return Task.CompletedTask;
        });
        var result = await fixture.Coordinator.TranslateAsync(Query, source, "en-US", Identity, fixture.Cache.Generation,
            (prompt, _) => { prompts.Add(prompt); return Task.FromResult(prompts.Count == 1 ? "I love you and miss you dearly" : "My world is full of sunshine"); }, default, progress);
        CollectionAssert.AreEqual(new[] { PlainHyLyricsProtocol.BuildPrompt(source.Lines[1].Text, "en-US"),
            PlainHyLyricsProtocol.BuildPrompt(source.Lines[2].Text, "en-US") }, prompts);
        Assert.IsTrue(progressCalls > 0);
        Assert.AreEqual(source.Lines[0], result.Document.Lines[0]);
        Assert.IsTrue(result.Document.Lines.Skip(1).All(line => line.TranslationOrigin == LyricsTranslationOrigin.LocalAi));
        var cached = await fixture.Coordinator.TryGetCachedAsync(Query, source, "en-US", Identity, default);
        Assert.IsNotNull(cached);
        CollectionAssert.AreEqual(result.Document.Lines.ToArray(), cached.Document.Lines.ToArray());
        using var json = JsonDocument.Parse((await fixture.Cache.ReadAsync(PlainHyLyricsProtocol.CacheKey(Query, source, "en-US", Identity), default))!);
        CollectionAssert.AreEqual(new[] { 1, 2 }, json.RootElement.EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToArray());
    }

    [TestMethod]
    public async Task PlainBackendAndResolverBypassBeforeRuntimeManifestOrModelAccess()
    {
        using var fixture = new Fixture();
        var runtime = new AiLyricsRuntimePackage(_ => throw new AssertFailedException("No runtime access is allowed on bypass."), Path.GetTempPath());
        using var runner = new RejectingRunner();
        using var backend = new PlainHyLyricsBackend(fixture.Coordinator, runner, runtime, Path.GetTempPath());
        using var models = new AiModelPackageService(Path.Combine(fixture.Root, "models"));
        var resolver = new PlainHyLyricsPackageResolver(models, runtime);
        foreach (var source in new[]
        {
            LyricsParser.Parse("[00:01]I will wait for you", LyricsProviderKind.NetEase, "[00:01]我会一直等待你"),
            LyricsParser.Parse("[00:00]作词：Someone\n[00:01]我的世界充满阳光", LyricsProviderKind.NetEase),
        })
        {
            Assert.IsNull(await resolver.ResolveAsync(AiLyricsModelCatalog.ExperimentalPlain.Id, Query, source, "zh-CN", default));
            Assert.IsNull(await backend.TryGetCachedResultAsync(AiLyricsModelCatalog.ExperimentalPlain.Id, Query, source, "zh-CN", default));
            var package = new AiLyricsResolvedPackage(PlainHyLyricsBackend.BackendId, Identity, "not-opened", "not-opened", null, CacheGeneration: fixture.Cache.Generation);
            var result = await backend.TranslateAsync(package, Query, source, "zh-CN", default);
            Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
            CollectionAssert.AreEqual(source.Lines.ToArray(), result.Document.Lines.ToArray());
        }
    }

    [TestMethod]
    public void ExplicitSourceLanguageAndEligibleIdsParticipateInAiCacheIdentity()
    {
        var source = LyricsParser.Parse("[00:01]世界", LyricsProviderKind.NetEase);
        var explicitChinese = source with { Lines = [source.Lines[0] with { SourceLanguage = "zh-CN" }] };
        Assert.AreNotEqual(PlainHyLyricsProtocol.CacheKey(Query, source, "zh-CN", Identity),
            PlainHyLyricsProtocol.CacheKey(Query, explicitChinese, "zh-CN", Identity));
    }

    private sealed class RejectingRunner : IPlainLyricsRunner
    {
        public Task<string> RunPlainAsync(string executablePath, string modelPath, string prompt, string stagingDirectory, CancellationToken cancellationToken, string verifiedModelSha256) => throw new AssertFailedException("No inference on bypass.");
        public Task DrainCleanupAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DropSpace-admission-" + Guid.NewGuid().ToString("N"));
        public AiLyricsCache Cache { get; }
        public PlainHyLyricsCoordinator Coordinator { get; }
        public Fixture() { Cache = new(Root); Coordinator = new(Cache); }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
