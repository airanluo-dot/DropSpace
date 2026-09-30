using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
namespace DropSpace.Core.Tests;
[TestClass]
public sealed class LyricsRepeatedLineRegressionTests
{
    [TestMethod]
    public void RepeatedEnhancedLrcLinesShiftTheirWordTimestamps()
    {
        var document = LyricsParser.Parse("[00:10.00][00:30.00]<00:10.00>Hello <00:12.00>world", LyricsProviderKind.LocalLrc);
        Assert.HasCount(2, document.Lines);
        Assert.AreEqual(TimeSpan.FromSeconds(30), document.Lines[1].Words[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(32), document.Lines[1].Words[0].End);
        var frame = new LyricsTimelineEngine().GetFrame(document, TimeSpan.FromSeconds(31), 0);
        Assert.AreEqual(0, frame.WordIndex);
        Assert.AreEqual(0.5, frame.WordProgress, 0.001);
    }
}
