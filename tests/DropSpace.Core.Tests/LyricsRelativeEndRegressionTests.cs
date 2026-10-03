using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsRelativeEndRegressionTests
{
    [TestMethod]
    public void RelativeWordEndUsesTheSameParentOriginAsItsBegin()
    {
        const string ttml = "<tt><body><p begin=\"10s\" end=\"20s\">" +
            "<span begin=\"1s\" end=\"2s\">first</span>" +
            "<span begin=\"2s\" end=\"3s\">second</span>" +
            "</p></body></tt>";
        var line = LyricsParser.Parse(ttml, LyricsProviderKind.Amll, ttmlTiming: TtmlTimingMode.ParentRelative).Lines.Single();

        Assert.AreEqual(TimeSpan.FromSeconds(11), line.Words[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(12), line.Words[0].End);
        Assert.AreEqual(TimeSpan.FromSeconds(12), line.Words[1].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(13), line.Words[1].End);
        var frame = new LyricsTimelineEngine().GetFrame(
            new([line], LyricsProviderKind.Amll), TimeSpan.FromSeconds(11.5), 0);
        Assert.AreEqual(0.5, frame.WordProgress, 0.001);
    }
}
