using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Ct2LyricsPipelineTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(9), "track", "Album artist");
    private static LyricsDocument Source(int count = 2) => new(Enumerable.Range(0, count).Select(i =>
        new LyricsLine(TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), $"source {i}", null,
            [new("word", TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1))])).ToArray(), LyricsProviderKind.LocalLrc);

    [TestMethod]
    [DataRow(null)]
    [DataRow("mixed")]
    [DataRow("und")]
    [DataRow("en")]
    public async Task UnknownMixedAndAlreadyTargetSourcesAbstain(string? language)
    {
        using var fixture = new Fixture();
        var resolver = fixture.Resolver(new Identifier(language));
        Assert.IsNull(await resolver.ResolveAsync("candidate", Query, Source(), "en", default));
        Assert.AreEqual(0, fixture.Packages.Calls);
        Assert.AreEqual(0, fixture.Runner.Calls.Count);
    }

    [TestMethod]
    public async Task DefaultIdentifierAbstainsWithoutCatalogLookup()
    {
        using var fixture = new Fixture();
        Assert.IsNull(await fixture.Resolver(new AbstainingLyricsSourceIdentifier())
            .ResolveAsync("candidate", Query, Source(), "en", default));
        Assert.AreEqual(0, fixture.Packages.Calls);
    }

    [TestMethod]
    [DataRow("zh-Hant")]
    [DataRow("de")]
    public async Task UnsupportedTargetAbstainsBeforeSourceIdentification(string target)
    {
        using var fixture = new Fixture();
        var detector = new Identifier("ja");
        Assert.IsNull(await fixture.Resolver(detector).ResolveAsync("candidate", Query, Source(), target, default));
        Assert.AreEqual(0, detector.Calls);
    }

    [TestMethod]
    public async Task DirectBackendPreservesOriginalIdsTimingsWordsAndCachesCompleteResult()
    {
        using var fixture = new Fixture();
        var source = Source();
        var package = await fixture.Resolve(source, "zh-CN");
        var result = await fixture.Backend.TranslateAsync(package, Query, source, "zh-CN", default);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.HasCount(1, fixture.Runner.Calls);
        Assert.AreEqual("ja>zh", fixture.Runner.Calls[0]);
        for (var i = 0; i < source.Lines.Count; i++)
        {
            Assert.AreEqual(source.Lines[i].Text, result.Document.Lines[i].Text);
            Assert.AreEqual(source.Lines[i].Start, result.Document.Lines[i].Start);
            Assert.AreEqual(source.Lines[i].End, result.Document.Lines[i].End);
            Assert.AreSame(source.Lines[i].Words, result.Document.Lines[i].Words);
            Assert.AreEqual("translated " + i, result.Document.Lines[i].Secondary);
        }
        await fixture.Backend.TranslateAsync(package, Query, source, "zh-CN", default);
        Assert.HasCount(1, fixture.Runner.Calls, "Whole-song cache should avoid a second helper call.");
    }

    [TestMethod]
    public async Task PivotUsesFirstLegTextAndRetainsAppIds()
    {
        using var fixture = new Fixture();
        fixture.Packages.Missing.Add(("ja", "zh"));
        fixture.Runner.Infer = (from, to, input, _) =>
        {
            if (from == "en") Assert.IsTrue(input.All(x => x.Text == "english " + x.Id));
            return Task.FromResult<IReadOnlyList<Ct2TranslationLine>>(input.Select(x => new Ct2TranslationLine(x.Id,
                (to == "en" ? "english " : "chinese ") + x.Id)).ToArray());
        };
        var source = Source();
        var result = await fixture.Backend.TranslateAsync(await fixture.Resolve(source, "zh"), Query, source, "zh", default);
        CollectionAssert.AreEqual(new[] { "ja>en", "en>zh" }, fixture.Runner.Calls);
        Assert.AreEqual("chinese 0", result.Document.Lines[0].Secondary);
    }

    [TestMethod]
    public async Task MissingPivotLegCannotPublishOrInfer()
    {
        using var fixture = new Fixture();
        fixture.Packages.Missing.UnionWith([("ja", "zh"), ("en", "zh")]);
        Assert.IsNull(await fixture.Resolver(new Identifier("ja")).ResolveAsync("candidate", Query, Source(), "zh", default));
        Assert.HasCount(0, fixture.Runner.Calls);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task PartialPivotLegRejectsEntireSongWithoutCaching(int failedLeg)
    {
        using var fixture = new Fixture();
        fixture.Packages.Missing.Add(("ja", "zh"));
        fixture.Runner.Infer = (_, _, input, _) => Task.FromResult<IReadOnlyList<Ct2TranslationLine>>(
            input.Take(fixture.Runner.Calls.Count == failedLeg ? 1 : input.Count).Select(x => new Ct2TranslationLine(x.Id, "translated")).ToArray());
        var source = Source(); var package = await fixture.Resolve(source, "zh");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Backend.TranslateAsync(package, Query, source, "zh", default));
        Assert.HasCount(failedLeg, fixture.Runner.Calls);
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(package, source, "zh"), default));
        Assert.IsTrue(source.Lines.All(x => x.Secondary is null));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("reordered")]
    [DataRow("control")]
    [DataRow("leak")]
    public async Task InvalidOutputIsRejectedWithoutCache(string kind)
    {
        using var fixture = new Fixture();
        fixture.Runner.Infer = (_, _, input, _) => Task.FromResult<IReadOnlyList<Ct2TranslationLine>>(kind switch
        {
            "null" => [null!, null!],
            "reordered" => [new(1, "translated"), new(0, "translated")],
            "control" => [new(0, "invalid\ntext"), new(1, "translated")],
            _ => [new(0, "<|im_start|>"), new(1, "translated")],
        });
        var source = Source(); var package = await fixture.Resolve(source);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Backend.TranslateAsync(package, Query, source, "en", default));
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(package, source), default));
    }

    [TestMethod]
    [DataRow("[null,null]")]
    [DataRow("[{\"id\":0,\"text\":\"bad\",\"extra\":true},{\"id\":1,\"text\":\"bad\"}]")]
    public async Task InvalidCacheIsIgnoredAndReplacedAfterCompleteInference(string invalid)
    {
        using var fixture = new Fixture();
        var source = Source(); var package = await fixture.Resolve(source);
        await fixture.Cache.WriteAsync(Key(package, source), invalid, default);
        Assert.AreEqual(LyricsTranslationOutcome.Translated,
            (await fixture.Backend.TranslateAsync(package, Query, source, "en", default)).Outcome);
        Assert.HasCount(1, fixture.Runner.Calls);
    }

    [TestMethod]
    public async Task ProviderTranslationPreventsIdentificationAndInference()
    {
        using var fixture = new Fixture();
        var source = Source();
        source = source with { Lines = [source.Lines[0] with { Secondary = "provider", TranslationLanguage = "en", TranslationOrigin = LyricsTranslationOrigin.Provider }, source.Lines[1]] };
        var detector = new Identifier("ja");
        Assert.IsNull(await fixture.Resolver(detector).ResolveAsync("candidate", Query, source, "en", default));
        Assert.AreEqual(0, detector.Calls);
        Assert.AreEqual("provider", source.Lines[0].Secondary);
    }

    [TestMethod]
    public async Task CancelledCompletedHelperOutputCannotPublishOrCache()
    {
        using var fixture = new Fixture(); using var stop = new CancellationTokenSource();
        fixture.Runner.Infer = (_, _, input, _) => { stop.Cancel(); return Translated(input); };
        var source = Source(); var package = await fixture.Resolve(source);
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Backend.TranslateAsync(package, Query, source, "en", stop.Token));
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(package, source), default));
    }

    [TestMethod]
    public async Task ClearDuringResolutionDoesNotAdmitOldGeneration()
    {
        using var fixture = new Fixture();
        var detector = new Identifier("ja") { BeforeResult = () => fixture.Cache.ClearAsync(default) };
        var source = Source();
        var package = await fixture.Resolver(detector).ResolveAsync("candidate", Query, source, "en", default);
        Assert.IsNotNull(package);
        var result = await fixture.Backend.TranslateAsync(package, Query, source, "en", default);
        Assert.AreSame(source, result.Document);
        Assert.HasCount(0, fixture.Runner.Calls);
    }

    [TestMethod]
    public async Task ClearDuringInferenceSuppressesPublicationAndStaleCacheWrite()
    {
        using var fixture = new Fixture();
        fixture.Runner.Infer = async (_, _, input, _) => { await fixture.Cache.ClearAsync(default); return await Translated(input); };
        var source = Source(); var package = await fixture.Resolve(source);
        var result = await fixture.Backend.TranslateAsync(package, Query, source, "en", default);
        Assert.AreSame(source, result.Document);
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(package, source), default));
    }

    [TestMethod]
    public async Task LongSongUsesBoundedBatchesAndCachesOnlyAfterAllBatches()
    {
        using var fixture = new Fixture();
        fixture.Runner.Infer = (_, _, input, _) => { Assert.IsTrue(input.Count <= 12); return Translated(input); };
        var source = Source(25); var package = await fixture.Resolve(source);
        var result = await fixture.Backend.TranslateAsync(package, Query, source, "en", default);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.HasCount(3, fixture.Runner.Calls);
    }

    [TestMethod]
    public async Task LaterBatchFailureCannotPersistEarlierSuccessfulLines()
    {
        using var fixture = new Fixture();
        fixture.Runner.Infer = (_, _, input, _) => fixture.Runner.Calls.Count == 2
            ? Task.FromResult<IReadOnlyList<Ct2TranslationLine>>([]) : Translated(input);
        var source = Source(13); var package = await fixture.Resolve(source);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Backend.TranslateAsync(package, Query, source, "en", default));
        Assert.HasCount(2, fixture.Runner.Calls);
        Assert.IsTrue(source.Lines.All(x => x.Secondary is null));
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(package, source), default));
    }

    [TestMethod]
    public async Task CopyOnlyOutputRemainsNeutralAndIsNotPersisted()
    {
        using var fixture = new Fixture();
        fixture.Runner.Infer = (_, _, input, _) => Task.FromResult<IReadOnlyList<Ct2TranslationLine>>(input.Select(x => new Ct2TranslationLine(x.Id, x.Text)).ToArray());
        var source = Source(); var package = await fixture.Resolve(source);
        var result = await fixture.Backend.TranslateAsync(package, Query, source, "en", default);
        Assert.AreSame(source, result.Document);
        Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
        Assert.IsNull(await fixture.Cache.ReadAsync(Key(package, source), default));
    }

    [TestMethod]
    public async Task DetectorAndProviderTrackEvidenceSeparateCacheKeys()
    {
        using var fixture = new Fixture(); var source = Source();
        var first = await fixture.Resolve(source);
        var detector = new Identifier("ja") { CacheIdentity = "detector-v2" };
        var second = await fixture.Resolver(detector).ResolveAsync("candidate", Query, source, "en", default);
        Assert.IsNotNull(second);
        var keys = new HashSet<string>
        {
            Key(first, source), Key(second, source),
            LyricsTranslationPrompt.CacheKey(Query with { AlbumArtist = "other" }, source, "en", first.CacheIdentity),
            LyricsTranslationPrompt.CacheKey(Query with { TrackIdentity = "other" }, source, "en", first.CacheIdentity),
            Key(first, source with { Provider = LyricsProviderKind.NetEase }),
        };
        Assert.HasCount(5, keys);
        var identity = fixture.Packages.Identity;
        fixture.Packages.Identity = identity with { RuntimeVersion = "runtime-v2" };
        Assert.AreNotEqual(first.CacheIdentity, (await fixture.Resolve(source)).CacheIdentity);
    }

    [TestMethod]
    public async Task BackendDrainsNativeCleanupBeforeMaintenanceAndBlocksNewAdmissions()
    {
        using var fixture = new Fixture();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runner.Cleanup = cleanup.Task;
        using var lifetime = new AiLyricsWorkLifetime(fixture.Backend.DrainCleanupAsync);
        var deleted = false;
        var maintenance = lifetime.MaintainAsync(_ => { deleted = true; return Task.CompletedTask; }, default);
        Assert.IsFalse(deleted);
        Assert.AreEqual(0, await lifetime.RunAsync(_ => Task.FromResult(1), 0, default));
        cleanup.SetResult();
        await maintenance;
        Assert.IsTrue(deleted);
    }

    [TestMethod]
    public async Task ResolvedSourceEvidenceCannotBeReusedForDifferentLyricsOrTrack()
    {
        using var fixture = new Fixture(); var source = Source();
        var package = await fixture.Resolve(source);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Backend.TranslateAsync(package,
            Query with { TrackIdentity = "other track" }, source, "en", default));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Backend.TranslateAsync(package,
            Query, source with { Lines = [source.Lines[0] with { Text = "other language" }, source.Lines[1]] }, "en", default));
        Assert.HasCount(0, fixture.Runner.Calls);
    }

    private static string Key(AiLyricsResolvedPackage package, LyricsDocument source, string target = "en") =>
        LyricsTranslationPrompt.CacheKey(Query, source, target, package.CacheIdentity);
    private static Task<IReadOnlyList<Ct2TranslationLine>> Translated(IReadOnlyList<Ct2SourceLine> input) =>
        Task.FromResult<IReadOnlyList<Ct2TranslationLine>>(input.Select(x => new Ct2TranslationLine(x.Id, "translated " + x.Id)).ToArray());

    private sealed class Identifier(string? language) : ILyricsSourceIdentifier
    {
        public string CacheIdentity { get; set; } = "fixture-detector-v1";
        public Func<Task>? BeforeResult { get; init; }
        public int Calls { get; private set; }
        public async Task<string?> IdentifyAsync(LyricsQuery query, LyricsDocument document, CancellationToken token)
        { Calls++; if (BeforeResult is not null) await BeforeResult(); token.ThrowIfCancellationRequested(); return language; }
    }
    private sealed class Packages : ICt2PackageResolver
    {
        public Ct2PackageIdentity Identity { get; set; } = new(HashA, HashA, HashB, Ct2TokenizerProtocol.ArgosSentencePiece, Ct2DecoderProtocol.Argos, HashA);
        public HashSet<(string, string)> Missing { get; } = [];
        public int Calls { get; private set; }
        public Task<(Ct2PackageReference Reference, Ct2PackageIdentity Identity)?> ResolveAsync(string sourceLanguage, string targetLanguage, CancellationToken token)
        {
            Calls++; token.ThrowIfCancellationRequested();
            return Task.FromResult<(Ct2PackageReference, Ct2PackageIdentity)?>(Missing.Contains((sourceLanguage, targetLanguage))
                ? null : (new("private-fixture", HashA), Identity));
        }
    }
    private sealed class Runner : ICt2InferenceRunner
    {
        public List<string> Calls { get; } = [];
        public Task Cleanup { get; set; } = Task.CompletedTask;
        public Func<string, string, IReadOnlyList<Ct2SourceLine>, CancellationToken, Task<IReadOnlyList<Ct2TranslationLine>>> Infer { get; set; } = (_, _, input, _) => Translated(input);
        public Task<IReadOnlyList<Ct2TranslationLine>> TranslateVerifiedAsync(Ct2PackageReference reference, Ct2PackageIdentity expectedIdentity,
            string source, string target, IReadOnlyList<Ct2SourceLine> lines, CancellationToken token)
        { Calls.Add($"{source}>{target}"); return Infer(source, target, lines, token); }
        public Task DrainCleanupAsync(CancellationToken token) => Cleanup.WaitAsync(token);
        public void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "DropSpace-ct2-pipeline-" + Guid.NewGuid().ToString("N"));
        internal AiLyricsCache Cache { get; }
        internal Packages Packages { get; } = new();
        internal Runner Runner { get; } = new();
        internal Ct2LyricsBackend Backend { get; }
        internal Fixture() { Cache = new(Root); Backend = new(Runner, Cache); }
        internal Ct2LyricsPackageResolver Resolver(ILyricsSourceIdentifier detector) => new("candidate", detector, Packages, Cache);
        internal async Task<AiLyricsResolvedPackage> Resolve(LyricsDocument source, string target = "en") =>
            await Resolver(new Identifier("ja")).ResolveAsync("candidate", Query, source, target, default) ?? throw new AssertFailedException("Fixture should resolve.");
        public void Dispose() { Backend.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
