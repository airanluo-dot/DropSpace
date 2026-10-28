using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsCandidateQualityTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(180));

    [TestMethod]
    public void DuplicateOverlappingOrUnrelatedWordsCannotInflateCoverage()
    {
        var line = new LyricsLine(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), "we sing together", null,
            [new("we", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
             new("sing", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)),
             new("we", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)),
             new("unrelated", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5))]);
        var document = new LyricsDocument([line], LyricsProviderKind.NetEase).Bind(Query, "Song", "Artist", "Album", 180, 12, "a");
        Assert.AreEqual(2d / 14, LyricsCandidateQuality.Describe(Query, document).WordCoverage, 0.000001);
        var complete = document with { Lines = [line with { Words =
            [new("we", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
             new("sing", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)),
             new("together", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7))] }] };
        Assert.AreEqual(1d, LyricsCandidateQuality.Describe(Query, complete).WordCoverage);
    }

    [TestMethod]
    public void CloserRecordingWinsOnlyWithinSameAdmittedSourcePriority()
    {
        LyricsSelectionCandidate Candidate(string id, double duration, LyricsProviderKind provider, string artist = "Artist") =>
            LyricsCandidateRules.Describe(id, LyricsParser.Parse("[00:01]We sing together", provider)
                .Bind(Query, "Song", artist, "Album", duration, 12, id), "");
        var close = Candidate("z-close", 180, LyricsProviderKind.NetEase);
        var farther = Candidate("a-farther", 187, LyricsProviderKind.NetEase);
        Assert.AreEqual(LyricsCandidateQuality.Describe(Query, Candidate("c0", 180.2, LyricsProviderKind.NetEase).Document).DurationCloseness,
            LyricsCandidateQuality.Describe(Query, Candidate("c1", 180.8, LyricsProviderKind.NetEase).Document).DurationCloseness);
        Assert.AreEqual("z-close", LyricsCandidateRules.Best([farther, close], Query, LyricsProviderKind.NetEase, null).Match?.CandidateId);
        var backup = Candidate("backup", 180, LyricsProviderKind.QqMusic);
        Assert.AreEqual("a-farther", LyricsCandidateRules.Best([backup, farther], Query, LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic).Match?.CandidateId);
        var wrong = Candidate("wrong", 180, LyricsProviderKind.NetEase, "Someone Else");
        Assert.AreEqual("a-farther", LyricsCandidateRules.Best([wrong, farther], Query, LyricsProviderKind.NetEase, null).Match?.CandidateId);
        var plain = LyricsParser.Parse("We sing together", LyricsProviderKind.NetEase).Bind(Query, "Song", "Artist", "Album", 180, 12, "plain");
        Assert.AreEqual(0d, LyricsCandidateQuality.Describe(Query, plain).TimedLineCoverage);
        var canonicalLive = close with { Id = "c0", Document = close.Document with
            { Match = close.Document.Match! with { CanonicalTitle = "Song (Live)" } } };
        Assert.AreEqual("a-farther", LyricsCandidateRules.Best([canonicalLive, farther], Query, LyricsProviderKind.NetEase, null).Match?.CandidateId);
        Assert.IsTrue(LyricsCandidateSelectionProtocol.TryBuild(Query, new([canonicalLive], 0), "", out var prompt));
        StringAssert.Contains(prompt, "Song (Live)");
        var oversized = canonicalLive with { Document = canonicalLive.Document with
            { Match = canonicalLive.Document.Match! with { CanonicalTitle = new string('x', 257) } } };
        Assert.IsFalse(LyricsCandidateSelectionProtocol.TryBuild(Query, new([oversized], 0), "", out _));
    }
}
