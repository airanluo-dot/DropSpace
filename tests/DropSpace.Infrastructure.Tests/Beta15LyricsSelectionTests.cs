using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta15LyricsSelectionTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(180), "beta15-fixture")
        { PreferredTranslationLanguage = "zh-Hans" };
    private static LyricsDocument Instrumental => (new LyricsDocument([], LyricsProviderKind.Lrclib)
        { BodyQuality = LyricsBodyQuality.ConfirmedInstrumental }).Bind(Query, "Song", "Artist", "Album", 180, 12, "instrumental");

    [TestMethod]
    public async Task VerifiedInstrumentalSurvivesBackupAndRemainingProviderFailures()
    {
        foreach (var selection in new[] { LyricsSelectionMode.Rules, LyricsSelectionMode.AiAssisted })
        {
            var source = new Provider(LyricsProviderKind.NetEase, _ => throw new HttpRequestException("fixture failure"));
            var backup = new Provider(LyricsProviderKind.Lrclib, _ => Instrumental);
            var other = new Provider(LyricsProviderKind.QqMusic, _ => throw new HttpRequestException("fixture failure"));
            var service = new LyricsService(new([source, backup, other]));
            var observed = new List<LyricsDocument>();
            var result = await service.QueryDetailedAsync(Query, new()
            {
                Enabled = true, Provider = LyricsProviderKind.NetEase, BackupProvider = LyricsProviderKind.Lrclib,
                SearchRemainingProviders = true, SelectionMode = selection,
            }, default, reportOriginal: observed.Add);
            Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
            Assert.AreEqual(LyricsBodyQuality.ConfirmedInstrumental, result.Document.BodyQuality);
            Assert.AreEqual("instrumental", result.Document.Match!.CandidateId);
            Assert.IsFalse(result.TranslationLookupIncomplete);
            Assert.IsFalse(LyricsTranslationPolicy.CanOfferLocalFallback(result));
            Assert.IsTrue(observed.Any(document => document.BodyQuality == LyricsBodyQuality.ConfirmedInstrumental));
            if (selection != LyricsSelectionMode.Rules)
                Assert.IsTrue(result.SelectionCandidates.Candidates.Any(candidate => candidate.Document.BodyQuality == LyricsBodyQuality.ConfirmedInstrumental));
        }
    }

    [TestMethod]
    public async Task VerifiedLyricBodyWinsAndUnknownEmptyEvidenceCannotBecomeInstrumental()
    {
        var actual = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "A lyric body", null, [])], LyricsProviderKind.QqMusic)
            .Bind(Query, "Song", "Artist", "Album", 180, 12, "lyrics");
        var service = new LyricsService(new([
            new Provider(LyricsProviderKind.Lrclib, _ => Instrumental),
            new Provider(LyricsProviderKind.QqMusic, _ => actual),
        ]));
        var result = await service.QueryDetailedAsync(Query, new()
        {
            Enabled = true, Provider = LyricsProviderKind.Lrclib, BackupProvider = LyricsProviderKind.QqMusic,
            SearchRemainingProviders = false, SelectionMode = LyricsSelectionMode.AiAssisted,
        }, default);
        Assert.AreEqual("lyrics", result.Document.Match!.CandidateId);
        Assert.AreEqual(LyricsBodyQuality.Usable, result.Document.BodyQuality);
        Assert.IsTrue(result.SelectionCandidates.Candidates.All(candidate => candidate.Document.Lines.Count > 0));
        foreach (var body in new[] { Instrumental with { Match = null },
            Instrumental.Bind(Query, "Other Song", "Artist", "Album", 180, 12, "wrong"),
            Instrumental with { BodyQuality = LyricsBodyQuality.NoLyrics },
            Instrumental with { BodyQuality = LyricsBodyQuality.RequestFailed } })
        {
            var empty = await new LyricsService(new([new Provider(LyricsProviderKind.Lrclib, _ => body)])).QueryDetailedAsync(Query,
                new() { Enabled = true, Provider = LyricsProviderKind.Lrclib, SearchRemainingProviders = false }, default);
            Assert.AreNotEqual(LyricsBodyQuality.ConfirmedInstrumental, empty.Document.BodyQuality);
            Assert.AreNotEqual(LyricsQueryStatus.Found, empty.Status);
            if (body.BodyQuality == LyricsBodyQuality.RequestFailed)
                Assert.AreEqual(LyricsQueryStatus.Failed, empty.Status);
        }
    }

    [TestMethod]
    public async Task ConfirmedInstrumentalCanBeCachedAndRetainedAcrossLaterProgressiveFailure()
    {
        var provider = new Provider(LyricsProviderKind.Lrclib, _ => Instrumental);
        var settings = new LyricsSettings { Enabled = true, Provider = LyricsProviderKind.Lrclib, SearchRemainingProviders = false };
        var service = new LyricsService(new([provider]));
        Assert.AreEqual(LyricsBodyQuality.ConfirmedInstrumental, (await service.QueryDetailedAsync(Query, settings, default)).Document.BodyQuality);
        Assert.AreEqual(LyricsBodyQuality.ConfirmedInstrumental, (await service.QueryDetailedAsync(Query, settings, default)).Document.BodyQuality);
        Assert.AreEqual(1, provider.Calls);
        var failed = await new LyricsService(new([new Progressive()])).QueryDetailedAsync(Query, settings, default);
        Assert.AreEqual(LyricsQueryStatus.Found, failed.Status);
        Assert.AreEqual(LyricsBodyQuality.ConfirmedInstrumental, failed.Document.BodyQuality);
    }

    private sealed class Provider(LyricsProviderKind kind, Func<LyricsQuery, LyricsDocument> read) : ILyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public int Calls { get; private set; }
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(read(query)); }
    }
    private sealed class Progressive : IProgressiveLyricsProvider
    {
        public LyricsProviderKind Kind => LyricsProviderKind.Lrclib;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken) =>
            QueryAsync(query, cancellationToken, _ => { });
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken, Action<LyricsDocument> reportCandidate)
        { reportCandidate(Instrumental); return Task.FromException<LyricsDocument>(new HttpRequestException("fixture later failure")); }
    }
}
