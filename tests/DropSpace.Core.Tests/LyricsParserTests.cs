using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsParserTests
{
    [TestMethod]
    public void ZeroTimeYrcCreditsDoNotReuseTheFirstSungTranslation()
    {
        var document = LyricsParser.Parse(
            "[0,0](0,0,0)Composer credit\n[0,0](0,0,0)Writer credit\n[0,3000](0,3000,0)first\n[4000,2000](4000,2000,0)second",
            LyricsProviderKind.NetEase, "[00:00.000]translated first\n[00:04.000]translated second");

        Assert.IsNull(document.Lines[0].Secondary);
        Assert.IsNull(document.Lines[1].Secondary);
        Assert.AreEqual("translated first", document.Lines[2].Secondary);
        Assert.AreEqual("translated second", document.Lines[3].Secondary);
    }

    [TestMethod]
    public void NearbyLrcCreditsDoNotStealAnExactTranslation()
    {
        var document = LyricsParser.Parse(
            "[00:00.000]Composer credit\n[00:00.131]Writer credit\n[00:00.262]first\n[00:01.755]second",
            LyricsProviderKind.NetEase, "[00:00.262]translated first\n[00:01.755]translated second");

        Assert.IsNull(document.Lines[0].Secondary);
        Assert.IsNull(document.Lines[1].Secondary);
        Assert.AreEqual("translated first", document.Lines[2].Secondary);
        Assert.AreEqual("translated second", document.Lines[3].Secondary);
    }

    [TestMethod]
    public void DeclaredYrcAndTtmlEndsDoNotExtendAcrossInstrumentalGaps()
    {
        foreach (var text in new[]
        {
            "[1000,1000](1000,1000,0)first\n[10000,1000](10000,1000,0)second",
            "<tt><body><p begin=\"1s\" end=\"2s\">first</p><p begin=\"10s\" end=\"11s\">second</p></body></tt>",
        })
        {
            var document = LyricsParser.Parse(text, LyricsProviderKind.Amll);
            Assert.AreEqual(TimeSpan.FromSeconds(2), document.Lines[0].End);
            Assert.IsNull(new LyricsTimelineEngine().GetFrame(document, TimeSpan.FromSeconds(5), 0).Line);
        }
    }

    [TestMethod]
    public void FinalTimedLineExpiresAtItsDeclaredEnd()
    {
        var document = LyricsParser.Parse(
            "[00:00.00]first\n[00:02.00]last",
            LyricsProviderKind.NetEase);
        var timeline = new LyricsTimelineEngine();

        Assert.AreEqual("last", timeline.GetFrame(document, TimeSpan.FromSeconds(2.5), 0).Line?.Text);
        Assert.IsNull(timeline.GetFrame(document, TimeSpan.FromSeconds(7), 0).Line);
    }

    [TestMethod]
    public void PlainLyricsAreBoundToTheKnownTrackDuration()
    {
        var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(180));
        var document = LyricsParser.Parse("first line\nsecond line", LyricsProviderKind.Lrclib)
            .Bind(query, "Song", "Artist", "Album", 180, 12, "candidate");

        Assert.AreEqual(1, document.Lines.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(180), document.Lines[0].End);
    }

    [TestMethod]
    public void AppleStyleTtmlPreservesWordTimingAndDoesNotDuplicateRubyText()
    {
        const string ttml = "<tt xmlns=\"http://www.w3.org/ns/ttml\" " +
            "xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" " +
            "xmlns:tts=\"http://www.w3.org/ns/ttml#styling\"><body><div><p begin=\"20s\" end=\"25s\">" +
            "<span begin=\"20s\" end=\"21s\">你</span>" +
            "<span ttm:role=\"x-translation\">you</span>" +
            "<span tts:ruby=\"container\"><span tts:ruby=\"base\">好</span>" +
            "<span tts:ruby=\"textContainer\"><span tts:ruby=\"text\" begin=\"21s\" end=\"22s\">hao</span></span></span>" +
            "</p></div></body></tt>";

        var line = LyricsParser.Parse(ttml, LyricsProviderKind.Amll).Lines.Single();

        Assert.AreEqual("你好", line.Text);
        Assert.AreEqual("you", line.Secondary);
        Assert.AreEqual(TimeSpan.FromSeconds(20), line.Words[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(21), line.Words[0].End);
    }

    [TestMethod]
    public void RelativeNestedTtmlTimingIsOffsetFromTheParent()
    {
        const string ttml = "<tt><body><p begin=\"10s\" end=\"20s\"><span begin=\"1s\" dur=\"2s\">word</span></p></body></tt>";

        var line = LyricsParser.Parse(ttml, LyricsProviderKind.Amll).Lines.Single();

        Assert.AreEqual(TimeSpan.FromSeconds(11), line.Words[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(13), line.Words[0].End);
    }

    [TestMethod]
    public void TtmlTimeUnitsAreCaseInsensitive()
    {
        const string ttml = "<tt><body><p begin=\"1S\" dur=\"2S\">word</p></body></tt>";

        var line = LyricsParser.Parse(ttml, LyricsProviderKind.Amll).Lines.Single();

        Assert.AreEqual(TimeSpan.FromSeconds(1), line.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(3), line.End);
    }

    [TestMethod]
    public void TtmlBreakPreservesTextSeparation()
    {
        const string ttml = "<tt><body><p begin=\"1s\" end=\"4s\"><span>first</span><br/><span>second</span></p></body></tt>";

        var line = LyricsParser.Parse(ttml, LyricsProviderKind.Amll).Lines.Single();

        Assert.AreEqual($"first{Environment.NewLine}second", line.Text);
    }

    [TestMethod]
    public void HourBasedLrcTimestampsAreParsedForLinesAndWords()
    {
        var document = LyricsParser.Parse(
            "[01:02:03.500]<01:02:03.500>hour<01:02:04.500>word",
            LyricsProviderKind.NetEase);

        var line = document.Lines.Single();

        Assert.AreEqual(TimeSpan.FromSeconds(3723.5), line.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(3724.5), line.Words[0].End);
    }
}
