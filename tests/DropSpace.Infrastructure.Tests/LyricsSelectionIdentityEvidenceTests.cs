using System.Diagnostics;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsSelectionIdentityEvidenceTests
{
    [TestMethod]
    public async Task MissingArtistCannotBecomeAiSuccessWhileNativeFallbackAndKnownWeakAliasesRemainUsable()
    {
        foreach (var mode in new[] { LyricsSelectionMode.AiAssisted, LyricsSelectionMode.AiRanked })
        foreach (var boundary in new[] { "target", "candidate", "album-artist-only" })
        {
            var query = new LyricsQuery("Song", boundary == "candidate" ? "Artist" : "", "Album",
                TimeSpan.FromSeconds(180), "identity:" + boundary, boundary == "album-artist-only" ? "Artist" : "")
            { CollectSelectionCandidates = true };
            var rules = Document(query, "Artist", false, LyricsProviderKind.NetEase);
            var candidate = Document(query, boundary == "candidate" ? " " : "Other Artist", true, LyricsProviderKind.QqMusic);
            if (boundary == "album-artist-only") candidate = candidate with { Match = candidate.Match! with { Artist = "Artist", Album = "" } };
            Assert.IsTrue(LyricsMatcher.CandidateScore(query, candidate.Match!.Title, candidate.Match.Artist,
                candidate.Match.Album, candidate.Match.DurationSeconds) >= 4,
                "This must exercise a genuinely admitted missing-artist case, not an already rejected match.");
            var runtime = new Runtime();
            var selector = new LyricsCandidateSelector(runtime);
            var settings = new LyricsSettings { SelectionMode = mode };
            var snapshot = Snapshot(rules, candidate);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await selector.SelectAsync(query, settings, "zh", "model", snapshot, rules, default);
                Assert.AreEqual(LyricsSelectionOutcome.Invalid, result.Outcome, $"{mode}/{boundary}");
                Assert.AreSame(rules, result.Document, "Rejecting AI confirmation must preserve the trusted original.");
            }
            Assert.AreEqual(boundary == "candidate" ? 2 : 0, runtime.Calls,
                "An absent target performer must skip inference; an unsupported model acceptance must not become a reusable success.");
        }

        // This remains an AI decision. The host must not demand literal artist
        // equality and thereby remove the useful cross-script candidate lane.
        var known = new LyricsQuery("Song", "Michael Jackson", "", TimeSpan.FromSeconds(180), "identity:known")
        { CollectSelectionCandidates = true };
        var original = Document(known, known.Artist, false, LyricsProviderKind.NetEase) with
        { Match = new("Song", known.Artist, "", 180, 12, "native") };
        var alias = Document(known, "迈克尔·杰克逊", true, LyricsProviderKind.QqMusic) with
        { Match = new("Song", "迈克尔·杰克逊", "", 180, 0, "alias") };
        Assert.AreEqual(0d, LyricsMatcher.Score(known, "Song", "迈克尔·杰克逊", "", 180));
        Assert.AreEqual(4d, LyricsMatcher.CandidateScore(known, "Song", "迈克尔·杰克逊", "", 180));
        foreach (var mode in new[] { LyricsSelectionMode.AiAssisted, LyricsSelectionMode.AiRanked })
        {
            var runtime = new Runtime();
            var selector = new LyricsCandidateSelector(runtime);
            var settings = new LyricsSettings { SelectionMode = mode };
            var snapshot = Snapshot(original, alias);
            Assert.AreEqual(LyricsSelectionOutcome.Selected,
                (await selector.SelectAsync(known, settings, "zh", "model", snapshot, original, default)).Outcome);
            Assert.AreEqual(LyricsSelectionOutcome.Reused,
                (await selector.SelectAsync(known, settings, "zh", "model", snapshot, original, default)).Outcome);
            Assert.AreEqual(1, runtime.Calls);
        }
    }

    private static LyricsDocument Document(LyricsQuery query, string artist, bool translated, LyricsProviderKind provider)
    {
        var document = LyricsParser.Parse("[00:01]The night is full of stars.", provider)
            .Bind(query, "Song", artist, "Album", 180, 12, translated ? "candidate" : "native");
        return document with { Lines = document.Lines.Select(line => line with
        {
            Secondary = translated ? "夜里满布繁星" : null,
            TranslationOrigin = translated ? LyricsTranslationOrigin.Provider : LyricsTranslationOrigin.None,
            TranslationLanguage = translated ? "zh" : null,
            TranslationLanguageIsExplicit = translated ? true : null,
        }).ToArray() };
    }
    private static LyricsCandidateSnapshot Snapshot(LyricsDocument rules, LyricsDocument candidate) => new(
        [LyricsCandidateRules.Describe("c0", rules, "zh"), LyricsCandidateRules.Describe("c1", candidate, "zh")],
        Stopwatch.GetTimestamp() + 12 * Stopwatch.Frequency);
    private sealed class Runtime : ILyricsSelectionRuntime
    {
        public int Calls { get; private set; }
        public bool CanPrepareSelection => true;
        public bool IsSelectionWarm(string hash) => true;
        public Task<bool> PrepareSelectionAsync(string path, string hash, CancellationToken token) => Task.FromResult(true);
        public Task<string?> TryRunSelectionAsync(string hash, string prompt, CancellationToken token)
        { Calls++; return Task.FromResult<string?>("{\"id\":\"c1\"}"); }
    }
}
