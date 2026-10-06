using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class Beta16WholeTrackAiAdmissionTests
{
    private static readonly LyricsQuery Query = new("song", "artist", "album", TimeSpan.FromSeconds(30), "recording");
    private static readonly LyricsSettings Settings = new() { Enabled = true, AiTranslationEnabled = true };

    [TestMethod]
    [DataRow("en", "I keep waiting for you", "en")]
    [DataRow("zh", "我们仍然等待明天的阳光", "zh")]
    public async Task OriginalTargetRetiresOldAiBeforeAnyCachePackageOrRuntime(string label, string text, string target)
    {
        using var fixture = new Fixture(label);
        var source = Source(text) with { Lines = [Source(text).Lines[0] with
            { Secondary = "Beta15 cached AI", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = target }] };
        var publication = await fixture.Service.TranslateForPublicationAsync(Query, source, Settings, target, default);
        Assert.AreEqual(LyricsWholeTrackDecision.OriginalTarget, publication.Document.TranslationAdmission!.Decision);
        Assert.IsNull(publication.Document.Lines[0].Secondary);
        fixture.AssertNoTranslationWork();
        Assert.AreEqual(1, fixture.Identifier.Calls);
    }

    [TestMethod]
    public async Task PartialNativeTargetWithNamesAndYeahBlocksWholeTrackAndKeepsGaps()
    {
        using var fixture = new Fixture("en");
        var source = Native(Source("I am waiting for tomorrow", "I am still here"));
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, source, Settings, "zh", default);
        Assert.AreEqual(LyricsWholeTrackDecision.ProviderTarget, result.TranslationAdmission!.Decision);
        Assert.AreEqual("我在等待 Frank Ocean，Yeah", result.Lines[0].Secondary);
        Assert.IsNull(result.Lines[1].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, result.Lines[0].TranslationOrigin);
        fixture.AssertNoTranslationWork();
    }

    [TestMethod]
    public async Task UnknownWithoutTargetNativeReachesExistingProgressiveTranslation()
    {
        using var fixture = new Fixture("fr");
        var updates = 0;
        var source = Source("Je reste toujours avec toi", "Je reste toujours avec toi");
        var prepared = LyricsLanguagePolicy.Prepare(source, "en", new("fr", 0.9, 0.02), generation: 17);
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { updates++; Assert.IsTrue(update.IsCurrent); return Task.CompletedTask; });
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, prepared, Settings, "en", default, progress);
        Assert.AreEqual(LyricsWholeTrackDecision.AllowAi, result.TranslationAdmission!.Decision);
        Assert.AreEqual(17L, result.TranslationAdmission.Generation);
        Assert.AreEqual(1, fixture.Backend.CacheCalls);
        Assert.AreEqual(1, fixture.Resolver.Calls);
        Assert.AreEqual(1, fixture.Backend.Calls);
        Assert.AreEqual(1, updates);
        Assert.AreEqual(0, fixture.Identifier.Calls, "A current whole-track snapshot is reused.");
        Assert.AreEqual("translated occurrence 0", result.Lines[0].Secondary);
        Assert.AreEqual("translated occurrence 1", result.Lines[1].Secondary);
    }

    [TestMethod]
    public async Task LatePartialNativeVetoRetiresQueuedProgressAndUncancelableFinal()
    {
        using var fixture = new Fixture("en", block: true);
        LyricsTranslationProgress? queued = null;
        var source = LyricsLanguagePolicy.Prepare(Source("I wait for your return", "I still wait here"), "zh",
            new("en", 0.9, 0.02), generation: 8);
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (update, _) => { queued = update; return Task.CompletedTask; });
        var pending = fixture.Service.TranslateForPublicationAsync(Query, source, Settings, "zh", default, progress);
        await fixture.Backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(queued);
        Assert.IsTrue(queued.IsCurrent);
        var native = LyricsLanguagePolicy.Prepare(Native(source), "zh", new("en", 0.9, 0.02), generation: 8);
        Assert.IsTrue(MediaExperienceService.IsLateNativeVeto(source, native));
        fixture.Service.ObserveSource(native);
        Assert.IsFalse(queued.IsCurrent);
        fixture.Backend.Complete.TrySetResult();
        var publication = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(publication.IsCurrent);
        Assert.IsFalse(publication.Document.Lines.Any(line => line.TranslationOrigin == LyricsTranslationOrigin.LocalAi));
        Assert.IsNull(native.Lines[1].Secondary, "A native veto does not fill its gaps with old AI.");
        Assert.AreEqual(AiLyricsTranslationState.Ready, fixture.Service.TranslationState);
    }

    [TestMethod]
    [DataRow("target")]
    [DataRow("generation")]
    [DataRow("candidate")]
    public async Task NewTargetGenerationOrCandidateRetiresFinalPublication(string change)
    {
        using var fixture = new Fixture("fr");
        var source = LyricsLanguagePolicy.Prepare(Source("Je reste toujours avec toi"), "en", new("fr", 0.9, 0.02), generation: 4);
        var publication = await fixture.Service.TranslateForPublicationAsync(Query, source, Settings, "en", default);
        Assert.IsTrue(publication.IsCurrent);
        var next = change switch
        {
            "target" => LyricsLanguagePolicy.Prepare(source, "zh", new("fr", 0.9, 0.02), generation: 4),
            "candidate" => LyricsLanguagePolicy.Prepare(source with { Match = source.Match! with { CandidateId = "other-candidate" } },
                "en", new("fr", 0.9, 0.02), generation: 4),
            _ => source with { TranslationAdmission = source.TranslationAdmission! with { Generation = 5 } },
        };
        fixture.Service.ObserveSource(next);
        Assert.IsFalse(publication.IsCurrent);
        var native = LyricsLanguagePolicy.Prepare(Native(next, next.TranslationAdmission!.Target),
            next.TranslationAdmission.Target, new("fr", 0.9, 0.02), generation: next.TranslationAdmission.Generation);
        Assert.AreEqual(LyricsWholeTrackDecision.ProviderTarget, native.TranslationAdmission!.Decision);
        Assert.AreEqual(change == "candidate", MediaExperienceService.IsLateNativeVeto(source, native),
            "A validated candidate for the same recording may veto AI; another target or generation may not.");
    }

    private static LyricsDocument Source(params string[] texts) => new(texts.Select((text, index) =>
        new LyricsLine(TimeSpan.FromSeconds(index * 3), TimeSpan.FromSeconds(index * 3 + 3), text, null, [])).ToArray(),
        LyricsProviderKind.NetEase, new("song", "artist", "album", 30, 10, "native-candidate", "recording"));

    private static LyricsDocument Native(LyricsDocument source, string target = "zh") => source with
    { Lines = source.Lines.Select((line, index) => index == 0 ? line with
        { Secondary = target == "zh" ? "我在等待 Frank Ocean，Yeah" : "I wait for Frank Ocean, Yeah",
            TranslationOrigin = LyricsTranslationOrigin.Provider,
            TranslationLanguage = target == "zh" ? "zh-Hans" : "en", TranslationLanguageIsExplicit = true } : line).ToArray() };

    private sealed class Identifier(string label) : ILyricsLanguageIdentifier
    {
        internal int Calls { get; private set; }
        public Task<LyricsDocument> PrepareAsync(LyricsDocument document, string targetLanguage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LyricsLanguagePolicy.IsAdmissionCurrent(document, targetLanguage)) return Task.FromResult(document);
            Calls++;
            return Task.FromResult(LyricsLanguagePolicy.Prepare(document, targetLanguage, new(label, 0.9, 0.02),
                generation: document.TranslationAdmission?.Generation ?? 0));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-Beta16-app-" + Guid.NewGuid().ToString("N"));
        internal Identifier Identifier { get; }
        internal Resolver Resolver { get; } = new();
        internal Backend Backend { get; }
        internal AiLyricsService Service { get; }
        internal Fixture(string label, bool block = false)
        {
            Identifier = new(label); Backend = new(block);
            var paths = new AppStoragePaths(_root);
            Service = new(paths, new LyricsCache(paths.Lyrics), NullLogger<AiLyricsService>.Instance,
                null, Resolver, Backend, languageIdentifier: Identifier);
        }
        internal void AssertNoTranslationWork()
        {
            Assert.AreEqual(0, Backend.CacheCalls);
            Assert.AreEqual(0, Resolver.Calls);
            Assert.AreEqual(0, Backend.Calls);
            Assert.AreEqual(0, Backend.Drains);
        }
        public void Dispose()
        {
            Backend.Complete.TrySetResult(); Service.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Resolver : IAiLyricsPackageResolver
    {
        internal int Calls { get; private set; }
        public Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query, LyricsDocument source,
            string targetLanguage, CancellationToken token)
        { Calls++; return Task.FromResult<AiLyricsResolvedPackage?>(new("beta16-spy", "identity", "model", "runtime", null)); }
    }

    private sealed class Backend(bool block) : IAiLyricsBackend
    {
        public string Id => "beta16-spy";
        internal int CacheCalls { get; private set; }
        internal int Calls { get; private set; }
        internal int Drains { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LyricsTranslationResult?> TryGetCachedResultAsync(string selectionId, LyricsQuery query, LyricsDocument source,
            string targetLanguage, CancellationToken token)
        { CacheCalls++; return Task.FromResult<LyricsTranslationResult?>(null); }
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query, LyricsDocument source,
            string targetLanguage, CancellationToken token) => throw new AssertFailedException("Progressive overload is required.");
        public async Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query, LyricsDocument source,
            string targetLanguage, CancellationToken token, LyricsTranslationProgressContext progress)
        {
            Calls++;
            var segments = LyricsLanguagePolicy.EligibleSegments(source, targetLanguage);
            var translated = source with { Lines = source.Lines.Select((line, index) => line with
                { Secondary = "translated occurrence " + index, TranslationOrigin = LyricsTranslationOrigin.LocalAi,
                    TranslationLanguage = targetLanguage, LocalAiAdmissionKey = LyricsLanguagePolicy.LocalAiAdmissionKey(source, index, targetLanguage, segments[index]) }).ToArray() };
            await progress.ReportAsync(translated, 0, source.Lines.Count, source.Lines.Count, token);
            Started.TrySetResult();
            if (block) await Complete.Task; // Deliberately models a backend that returns after cancellation.
            return new(translated, LyricsTranslationOutcome.Translated);
        }
        public Task DrainCleanupAsync(CancellationToken token) { Drains++; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
