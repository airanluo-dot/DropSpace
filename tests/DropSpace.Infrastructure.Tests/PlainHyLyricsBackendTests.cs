using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PlainHyLyricsBackendTests
{
    private static readonly LyricsQuery Query = new("host-only song", "host-only artist", "host-only album", TimeSpan.FromSeconds(90), "stable-track", "album-artist");
    private const string RuntimeHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static string Identity => PlainHyLyricsProtocol.InferenceIdentity(RuntimeHash);
    private static LyricsDocument Source(params string[] values) => new(values.Select((text, index) =>
        new LyricsLine(TimeSpan.FromSeconds(index), TimeSpan.FromSeconds(index + 1), text, null, [])).ToArray(), LyricsProviderKind.LocalLrc);

    [TestMethod]
    public void ExactOfficialTemplateAndSamplerIdentityRemainFrozen()
    {
        Assert.AreEqual("将以下文本翻译为英语，注意只需要输出翻译后的结果，不要额外解释：\n原句", PlainHyLyricsProtocol.BuildPrompt("原句", "en-US"));
        Assert.AreEqual("将以下文本翻译为简体中文，注意只需要输出翻译后的结果，不要额外解释：\nOriginal", PlainHyLyricsProtocol.BuildPrompt("Original", "zh-CN"));
#pragma warning disable MSTEST0032 // Intentional frozen public protocol/budget contract checks.
        Assert.AreEqual("official-plain-per-line-v1", PlainHyLyricsProtocol.Version);
        Assert.AreEqual("host-mapped-id-text-v1", PlainHyLyricsProtocol.HostMappingVersion);
        Assert.AreEqual(300, PlainHyLyricsProtocol.WholeSongSeconds);
#pragma warning restore MSTEST0032
        Assert.ThrowsExactly<ArgumentException>(() => PlainHyLyricsProtocol.BuildPrompt("original", "zh-Hant"));
        Assert.ThrowsExactly<InvalidDataException>(() => PlainHyLyricsProtocol.BuildPrompt(new string('x', 2000), "en"));
    }

    [TestMethod]
    public async Task InfersEveryUnknownSourceLineWithoutGoldOrScriptBypassAndMapsIdsInHost()
    {
        using var f = new Fixture(); var source = Source("Already English", "原句", "名前", "이름");
        var prompts = new List<string>();
        var result = await f.Translate(source, (prompt, _) => { prompts.Add(prompt); return Task.FromResult("translation " + prompts.Count); });
        Assert.HasCount(4, prompts);
        for (var i = 0; i < 4; i++)
        {
            Assert.AreEqual(PlainHyLyricsProtocol.BuildPrompt(source.Lines[i].Text, "en-US"), prompts[i]);
            Assert.AreEqual(source.Lines[i].Text, result.Document.Lines[i].Text);
            Assert.AreEqual(source.Lines[i].Start, result.Document.Lines[i].Start);
            Assert.AreEqual(source.Lines[i].End, result.Document.Lines[i].End);
            Assert.AreEqual("translation " + (i + 1), result.Document.Lines[i].Secondary);
            Assert.IsFalse(prompts[i].Contains(Query.Title, StringComparison.Ordinal));
        }
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        var again = await f.Translate(source, (_, _) => throw new AssertFailedException("A complete cache hit must avoid inference."));
        Assert.AreEqual(result.Document.Lines[0].Secondary, again.Document.Lines[0].Secondary);
    }

    [TestMethod]
    public async Task CopiedUnknownSameTargetOrNameIsNeutralAndMemoizedOnlyInCurrentGeneration()
    {
        using var f = new Fixture(); var source = Source("Tokyo", "Already English"); int calls = 0;
        Task<string> Infer(string prompt, CancellationToken _) { calls++; return Task.FromResult(prompt[(prompt.IndexOf('\n') + 1)..]); }
        var first = await f.Translate(source, Infer);
        Assert.AreSame(source, first.Document); Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, first.Outcome);
        Assert.AreEqual(2, calls); Assert.IsNull(await f.Cache.ReadAsync(f.Key(source), default));
        await f.Translate(source, Infer); Assert.AreEqual(2, calls);
        await f.Cache.ClearAsync(default);
        await f.Translate(source, Infer); Assert.AreEqual(4, calls);
    }

    [TestMethod]
    public async Task UsefulAndUnchangedLinesFormOneCompleteHostMappedDocument()
    {
        using var f = new Fixture(); var source = Source("Name", "原句"); int calls = 0;
        var result = await f.Translate(source, (_, _) => Task.FromResult(++calls == 1 ? "Name" : "Translated original"));
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.IsNull(result.Document.Lines[0].Secondary);
        Assert.AreEqual("Translated original", result.Document.Lines[1].Secondary);
        var saved = await f.Cache.ReadAsync(f.Key(source), default);
        Assert.IsNotNull(saved); StringAssert.Contains(saved, "\"id\":0"); StringAssert.Contains(saved, "\"id\":1");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("first\nsecond")]
    [DataRow("```text")]
    [DataRow("<|im_start|>")]
    public async Task LaterInvalidLineNeverPublishesOrCachesEarlierTranslationAndDoesNotMemoize(string invalid)
    {
        using var f = new Fixture(); var source = Source("first", "second"); int calls = 0;
        var result = await f.Translate(source, (_, _) => Task.FromResult(++calls == 1 ? "translated" : invalid));
        Assert.AreEqual(LyricsTranslationOutcome.Failed, result.Outcome); Assert.AreSame(source, result.Document);
        Assert.IsNull(await f.Cache.ReadAsync(f.Key(source), default));
        var retry = await f.Translate(source, (_, _) => { calls++; return Task.FromResult("translated"); });
        Assert.AreEqual(4, calls); Assert.AreEqual(LyricsTranslationOutcome.Translated, retry.Outcome);
    }

    [TestMethod]
    public async Task CancellationAndWholeSongBudgetDoNotCacheOrSuppressRetry()
    {
        using var f = new Fixture(); var source = Source("first", "second");
        using var external = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Translate(source, (_, _) =>
        { external.Cancel(); return Task.FromResult("translated"); }, external.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Coordinator.TranslateWithBudgetAsync(Query, source, "en", Identity,
            f.Cache.Generation, async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "unreachable"; }, default, TimeSpan.FromMilliseconds(15)));
        Assert.IsNull(await f.Cache.ReadAsync(f.Key(source), default));
        Assert.AreEqual(LyricsTranslationOutcome.Translated,
            (await f.Translate(source, (_, _) => Task.FromResult("translated"))).Outcome);
    }

    [TestMethod]
    public async Task ProviderPrecedenceAndClearGenerationArePreserved()
    {
        using var f = new Fixture(); var source = Source("first");
        var provider = source with { Lines = [source.Lines[0] with { Secondary = "provider", TranslationLanguage = "en", TranslationOrigin = LyricsTranslationOrigin.Provider }] };
        var result = await f.Translate(provider, (_, _) => throw new AssertFailedException("Provider translation must win."));
        Assert.AreSame(provider, result.Document);
        result = await f.Translate(source, async (_, _) => { await f.Cache.ClearAsync(default); return "translated"; });
        Assert.AreSame(source, result.Document); Assert.IsNull(await f.Cache.ReadAsync(f.Key(source), default));
    }

    [TestMethod]
    public async Task BackendCacheIsIsolatedFromLegacyJsonAndInvalidCompleteEntries()
    {
        using var f = new Fixture(); var source = Source("first", "second");
        await f.Cache.WriteAsync(LyricsTranslationPrompt.CacheKey(Query, source, "en", AiLyricsModelCatalog.ExperimentalPlain.Sha256),
            "[{\"id\":0,\"text\":\"wrong protocol\"},{\"id\":1,\"text\":\"wrong protocol\"}]", default);
        Assert.IsNull(await f.Coordinator.TryGetCachedAsync(Query, source, "en", Identity, default));
        await f.Cache.WriteAsync(f.Key(source), "[null,null]", default);
        Assert.IsNull(await f.Coordinator.TryGetCachedAsync(Query, source, "en", Identity, default));
        Assert.AreNotEqual(f.Key(source), PlainHyLyricsProtocol.CacheKey(Query, source, "en", PlainHyLyricsProtocol.InferenceIdentity(new string('b', 64))));
    }

    [TestMethod]
    public async Task ActualBackendUsesPlainRunnerPinnedModelAndBackendSpecificCache()
    {
        using var f = new Fixture();
        var runtime = new AiLyricsRuntimePackage(_ => new MemoryStream("trusted fixture manifest"u8.ToArray()), Path.GetTempPath());
        using var runner = new RecordingPlainRunner();
        using var backend = new PlainHyLyricsBackend(f.Coordinator, runner, runtime, Path.GetTempPath());
        var identity = PlainHyLyricsProtocol.InferenceIdentity(runtime.GetManifestCacheIdentity());
        var source = Source("source one", "source two");
        var package = new AiLyricsResolvedPackage(PlainHyLyricsBackend.BackendId, identity, "verified model", "verified runtime", null,
            CacheGeneration: f.Cache.Generation);
        var translated = await backend.TranslateAsync(package, Query, source, "en", default);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, translated.Outcome);
        Assert.AreEqual(2, runner.Calls);
        CollectionAssert.AreEqual(new[] { PlainHyLyricsProtocol.BuildPrompt("source one", "en"), PlainHyLyricsProtocol.BuildPrompt("source two", "en") }, runner.Prompts);
        Assert.IsNotNull(await backend.TryGetCachedResultAsync(AiLyricsModelCatalog.ExperimentalPlain.Id, Query, source, "en", default));
        Assert.IsNull(await backend.TryGetCachedResultAsync(AiLyricsModelCatalog.Standard.Id, Query, source, "en", default));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => backend.TranslateAsync(package with { CacheIdentity = new string('b', 64) }, Query, source, "en", default));
        Assert.AreEqual(2, runner.Calls);
        await backend.DrainCleanupAsync(default); Assert.AreEqual(1, runner.Drains);
    }

    [TestMethod]
    public async Task ShippingResolverRejectsLegacyProfilesBeforeAnyRuntimeExtraction()
    {
        using var f = new Fixture();
        var runtime = new AiLyricsRuntimePackage(_ => throw new AssertFailedException("Legacy profile must not read or extract runtime."), Path.GetTempPath());
        using var models = new AiModelPackageService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var resolver = new PlainHyLyricsPackageResolver(models, runtime);
        foreach (var legacy in AiLyricsModelCatalog.Legacy)
            Assert.IsNull(await resolver.ResolveAsync(legacy.Id, Query, Source("text"), "en", default));
    }

    private sealed class RecordingPlainRunner : IPlainLyricsRunner
    {
        internal int Calls { get; private set; }
        internal int Drains { get; private set; }
        internal List<string> Prompts { get; } = [];
        public Task<string> RunPlainAsync(string executablePath, string modelPath, string prompt, string stagingDirectory,
            CancellationToken cancellationToken, string verifiedModelSha256)
        {
            Assert.AreEqual(AiLyricsModelCatalog.ExperimentalPlain.Sha256, verifiedModelSha256);
            Calls++; Prompts.Add(prompt); return Task.FromResult("translated " + Calls);
        }
        public Task DrainCleanupAsync(CancellationToken token) { Drains++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-plain-beta-" + Guid.NewGuid().ToString("N"));
        internal AiLyricsCache Cache { get; }
        internal PlainHyLyricsCoordinator Coordinator { get; }
        internal Fixture() { Cache = new(_root); Coordinator = new(Cache); }
        internal Task<LyricsTranslationResult> Translate(LyricsDocument source, Func<string, CancellationToken, Task<string>> infer, CancellationToken token = default) =>
            Coordinator.TranslateAsync(Query, source, "en-US", Identity, Cache.Generation, infer, token);
        internal string Key(LyricsDocument source) => PlainHyLyricsProtocol.CacheKey(Query, source, "en-US", Identity);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
