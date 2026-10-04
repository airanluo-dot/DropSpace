using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsCandidateSelectionTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(180), "track");
    private static readonly LyricsSelectionCandidate Candidate = LyricsCandidateRules.Describe("c0",
        LyricsParser.Parse("[00:01]PRIVATE LYRIC SENTINEL", LyricsProviderKind.NetEase)
            .Bind(Query, "Song", "Artist", "Album", 180, 12, "source-id"), "zh");

    [TestMethod]
    public void SelectorOnlyAcceptsListedIdOrExplicitAbstentionAndNeverSendsLyrics()
    {
        var snapshot = new LyricsCandidateSnapshot([Candidate], 0);
        Assert.IsTrue(LyricsCandidateSelectionProtocol.TryBuild(Query, snapshot, "zh", out var prompt));
        Assert.IsFalse(prompt.Contains("PRIVATE LYRIC SENTINEL", StringComparison.Ordinal));
        Assert.IsTrue(LyricsCandidateSelectionProtocol.TryParse("{\"id\":\"c0\"}", [Candidate], out var id));
        Assert.AreEqual("c0", id);
        Assert.IsTrue(LyricsCandidateSelectionProtocol.TryParse("{\"id\":null}", [Candidate], out id));
        Assert.IsNull(id);
        foreach (var invalid in new[] { "{\"id\":\"c9\"}", "{\"id\":\"c0\",\"id\":\"c0\"}",
            "{\"id\":\"c0\",\"reason\":\"yes\"}", "```json\n{\"id\":\"c0\"}\n```" })
            Assert.IsFalse(LyricsCandidateSelectionProtocol.TryParse(invalid, [Candidate], out _), invalid);
        Assert.IsFalse(LyricsCandidateSelectionProtocol.TryBuild(Query, snapshot with { Truncated = true }, "zh", out _));
        Assert.IsFalse(LyricsCandidateSelectionProtocol.TryBuild(Query, snapshot with { Candidates = [Candidate, Candidate] }, "zh", out _));
    }

    [TestMethod]
    public void DecisionIdentitySeparatesModeModelTargetTrackAndSourceRevision()
    {
        var snapshot = new LyricsCandidateSnapshot([Candidate], 0);
        var settings = new LyricsSettings();
        string Key(LyricsQuery query, LyricsSettings preference, string target, string model, LyricsCandidateSnapshot input) =>
            LyricsCandidateSelectionProtocol.DecisionKey(query, preference, target, model, input, "metadata");
        var baseline = Key(Query, settings, "zh", "model1", snapshot);
        Assert.AreNotEqual(baseline, Key(Query, settings with { SelectionMode = LyricsSelectionMode.AiAssisted }, "zh", "model1", snapshot));
        Assert.AreNotEqual(baseline, Key(Query, settings, "en", "model1", snapshot));
        Assert.AreNotEqual(baseline, Key(Query, settings, "zh", "model2", snapshot));
        Assert.AreNotEqual(baseline, Key(Query with { TrackIdentity = "other" }, settings, "zh", "model1", snapshot));
        Assert.AreNotEqual(baseline, Key(Query, settings, "zh", "model1", snapshot with
        { Candidates = [Candidate with { Document = Candidate.Document with { ProviderDataRevision = 2 } }] }));
    }
}
