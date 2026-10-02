using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class CompactLyricsLayoutTests
{
    [TestMethod]
    public void UntimedParagraphsAreFullSingleLineMarqueesOnlyAtPresentation()
    {
        const string original = "作词：Someone\nI love you\n\n君の声が聞こえる\nLast line";
        var document = LyricsParser.Parse(original, LyricsProviderKind.NetEase);
        var source = document.Lines.Single();
        var line = source with { Secondary = "AI translation\r\nNext verse\u2028Last verse",
            TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "en-US" };
        var displayed = LyricsDisplayPolicy.CompactText(line.Text);
        var translated = LyricsDisplayPolicy.CompactText(LyricsDisplayPolicy.SecondaryPresentation(line, "en-US", true));
        Assert.AreEqual("作词：Someone I love you  君の声が聞こえる Last line", displayed);
        Assert.AreEqual("AI · AI translation Next verse Last verse", translated);
        Assert.AreEqual(string.Join(Environment.NewLine, original.Split('\n')), source.Text);
        Assert.AreEqual(source.Start, line.Start);
        Assert.AreEqual(source.End, line.End);
        Assert.AreEqual("AI translation\r\nNext verse\u2028Last verse", line.Secondary);
        Assert.AreEqual("word ", LyricsDisplayPolicy.CompactText("word "), "Word timing prefixes retain their spaces.");
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.25d)]
    [DataRow(1.5d)]
    [DataRow(2d)]
    public void TwoMeasuredRowsAndAccessibilitySizesShareTheBodyHeight(double scale)
    {
        // Injected DIP measurements exercise geometry, not native glyph measurement.
        foreach (var measured in new[] { 40d, 54.5, 78, 112, 154, 208, 300 })
        foreach (var width in new[] { 180d, 280d, 460d })
        {
            var contentHeight = IslandGeometry.MusicCompactHeight(measured);
            var body = IslandGeometry.ForMusicCompact(width, measured, scale);
            Assert.AreEqual(contentHeight * scale, body.Height, .000001);
            Assert.IsTrue(body.Height >= measured * scale);
            Assert.IsTrue(body.Radius <= Math.Min(body.Width, body.Height) / 2);
        }
    }

    [TestMethod]
    public void OffBodyTranslationIsNotVisibleEvenWhenItHasPositiveDimensions()
    {
        Assert.IsFalse(LyricsDisplayPolicy.IntersectsViewport(20, 103, 160, 30, 280, 100));
        Assert.IsFalse(LyricsDisplayPolicy.IntersectsViewport(20, 119, 160, 30, 280, 100));
        Assert.IsTrue(LyricsDisplayPolicy.IntersectsViewport(20, 91, 160, 30, 280, 100));
        Assert.AreEqual(40d, IslandGeometry.MusicCompactHeight(double.NaN));
        Assert.AreEqual(340d, IslandGeometry.MusicCompactHeight(1000));
    }
}
