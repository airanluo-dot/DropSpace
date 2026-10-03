using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsPreviewPolicyTests
{
    private static readonly LyricsLine First = Line(2, 5, "First", "First translation");
    private static readonly LyricsLine Second = Line(8, 11, "Second", "Second translation");
    private static readonly LyricsLine Last = Line(14, 17, "Last");
    private static readonly LyricsDocument Document = new([First, Second, Last], LyricsProviderKind.LocalLrc);

    [TestMethod]
    [DataRow(0d, "Second")]
    [DataRow(3d, "Second")]
    [DataRow(7d, "Second")]
    [DataRow(8d, "Last")]
    [DataRow(13d, "Last")]
    public void IntroCurrentLineAndInterludePreviewTheNextOriginal(double seconds, string expected)
    {
        var position = TimeSpan.FromSeconds(seconds);
        var frame = new LyricsTimelineEngine().GetFrame(Document, position, 0);
        Assert.AreEqual(expected, LyricsPreviewPolicy.NextLine(Document.Lines, frame, position, 0)?.Text);
    }

    [TestMethod]
    public void LastLineOutroEmptyAndInstrumentalDocumentsHaveNoPlaceholder()
    {
        var engine = new LyricsTimelineEngine();
        foreach (var seconds in new[] { 14d, 18d, 120d })
        {
            var position = TimeSpan.FromSeconds(seconds);
            Assert.IsNull(LyricsPreviewPolicy.NextLine(Document.Lines, engine.GetFrame(Document, position, 0), position, 0));
        }
        Assert.IsNull(LyricsPreviewPolicy.NextLine([], LyricsHighlightFrame.Empty, TimeSpan.Zero, 0));
        Assert.IsNull(LyricsPreviewPolicy.NextLine([Line(0, 180, "纯音乐，请欣赏")], LyricsHighlightFrame.Empty, TimeSpan.Zero, 0));
        Assert.IsNull(LyricsPreviewPolicy.NextLine([Line(0, 2, ""), Line(2, 4, " ")], LyricsHighlightFrame.Empty, TimeSpan.Zero, 0));
    }

    [TestMethod]
    public void ForwardAndBackwardSeekingUseTheNewClock()
    {
        var engine = new LyricsTimelineEngine();
        foreach (var seconds in new[] { 9d, 3d, 15d, 3d })
        {
            var position = TimeSpan.FromSeconds(seconds);
            var expected = seconds == 9 ? "Last" : seconds == 3 ? "Second" : null;
            Assert.AreEqual(expected, LyricsPreviewPolicy.NextLine(Document.Lines, engine.GetFrame(Document, position, 0), position, 0)?.Text);
        }
    }

    [TestMethod]
    public void SeekDoesNotPreviewAPastLineWhileHighlightFrameCatchesUp()
    {
        var engine = new LyricsTimelineEngine();
        var beforeSeek = engine.GetFrame(Document, TimeSpan.FromSeconds(3), 0);
        Assert.IsNull(LyricsPreviewPolicy.NextLine(Document.Lines, beforeSeek, TimeSpan.FromSeconds(9), 0));
        var afterSeek = engine.GetFrame(Document, TimeSpan.FromSeconds(9), 0);
        Assert.IsNull(LyricsPreviewPolicy.NextLine(Document.Lines, afterSeek, TimeSpan.FromSeconds(3), 0));
    }

    [TestMethod]
    [DataRow(7d, 1000, "Last")]
    [DataRow(8d, -1000, "Second")]
    [DataRow(7d, 60000, null)]
    [DataRow(31d, -60000, "Second")]
    public void OffsetUsesTheSameClampedTimelineAsCurrentLyrics(double seconds, int delay, string? expected)
    {
        var position = TimeSpan.FromSeconds(seconds);
        var frame = new LyricsTimelineEngine().GetFrame(Document, position, delay);
        Assert.AreEqual(expected, LyricsPreviewPolicy.NextLine(Document.Lines, frame, position, delay)?.Text);
    }

    [TestMethod]
    public void SimultaneousAndWordSyncedLinesAreNotTheirOwnPreview()
    {
        var firstVariant = First with { Text = "First variant", Words = [new("First", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3))] };
        var secondVariant = Second with { Text = "Second variant" };
        var document = new LyricsDocument([First, firstVariant, Second, secondVariant, Last], LyricsProviderKind.LocalLrc);
        var engine = new LyricsTimelineEngine();
        Assert.AreSame(secondVariant, LyricsPreviewPolicy.NextLine(document.Lines,
            engine.GetFrame(document, TimeSpan.FromSeconds(3), 0), TimeSpan.FromSeconds(3), 0));
        Assert.AreSame(Last, LyricsPreviewPolicy.NextLine(document.Lines,
            engine.GetFrame(document, TimeSpan.FromSeconds(9), 0), TimeSpan.FromSeconds(9), 0));
    }

    [TestMethod]
    public void EmptyCuesAreSkippedWithoutShowingTranslationAsTheNextOriginal()
    {
        var document = new LyricsDocument([First, Line(6, 7, "", "Only a translation"),
            Line(7, 8, " \t"), Second], LyricsProviderKind.LocalLrc);
        Assert.AreSame(Second, LyricsPreviewPolicy.NextLine(document.Lines,
            new(First, -1, 0, 1), TimeSpan.FromSeconds(3), 0));
    }

    [TestMethod]
    public void TranslationReplacementDoesNotAffectNextOriginalOrMutateDocument()
    {
        var frame = new LyricsHighlightFrame(First, -1, 0, 1);
        var translated = First with { Secondary = "新的翻译", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "zh-CN" };
        LyricsLine[] lines = [translated, Second, Last];
        Assert.AreSame(Second, LyricsPreviewPolicy.NextLine(lines, frame, TimeSpan.FromSeconds(3), 0));
        Assert.AreSame(translated, lines[0]);
        Assert.AreSame(First, frame.Line);
        Assert.AreEqual("First translation", First.Secondary);
    }

    [TestMethod]
    public void StaleFrameFromAnotherDocumentHasNoPreview()
    {
        var stale = new LyricsHighlightFrame(First with { Text = "Another song" }, -1, 0, 1);
        Assert.IsNull(LyricsPreviewPolicy.NextLine(Document.Lines, stale, TimeSpan.FromSeconds(3), 0));
    }

    private static LyricsLine Line(int start, int end, string text, string? translation = null) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, translation, []);
}
