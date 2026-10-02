using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsDisplayPolicyTests
{
    private static readonly LyricsLine First = new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "First", "Translation", []);
    private static readonly LyricsLine Next = new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), "Next", null, []);

    [TestMethod]
    [DataRow(-10000, 20d, 0d)]
    [DataRow(10000, 5d, 60d)]
    [DataRow(0, 15d, 60d)]
    [DataRow(0, 100d, 180d)]
    public void CompactLineScrollUsesSameEffectiveTimeline(int delay, double position, double expected)
    {
        var line = new LyricsLine(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), "Long lyric", null, []);
        Assert.AreEqual(expected, LyricsDisplayPolicy.CompactScrollOffset(line, TimeSpan.FromSeconds(position),
            delay, false, 360, 360, 180), 0.001);
    }

    [TestMethod]
    public void DisablingWordHighlightDoesNotPinMarqueeToTail()
    {
        var line = LyricsParser.Parse("[10000,20000](10000,10000,0)Long lyric", DropSpace.Core.Models.LyricsProviderKind.NetEase).Lines.Single();
        Assert.IsNotEmpty(line.Words);
        Assert.AreEqual(0d, LyricsDisplayPolicy.CompactScrollOffset(line, TimeSpan.FromSeconds(10), 0, false, 360, 360, 180));
        Assert.AreEqual(60d, LyricsDisplayPolicy.CompactScrollOffset(line, TimeSpan.FromSeconds(15), 0, false, 360, 360, 180));
        Assert.AreEqual(0d, LyricsDisplayPolicy.CompactScrollOffset(line, TimeSpan.FromSeconds(10), 0, true, 0, 360, 180));
        Assert.AreEqual(180d, LyricsDisplayPolicy.CompactScrollOffset(line, TimeSpan.FromSeconds(20), 0, true, 360, 360, 180));
    }

    [TestMethod]
    public void AiPresentationIsLabeledWithoutChangingStoredTranslation()
    {
        var line = new LyricsLine(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Original", "Translation", [])
        { TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "en-US" };
        Assert.AreEqual("AI · Translation", LyricsDisplayPolicy.SecondaryPresentation(line, "en-GB", true));
        Assert.AreEqual("Translation", line.Secondary);
        Assert.AreEqual("Translation", LyricsDisplayPolicy.SecondaryPresentation(line with { TranslationOrigin = LyricsTranslationOrigin.Provider }, "en-US", true));
        Assert.IsNull(LyricsDisplayPolicy.SecondaryPresentation(line, "en-US", false));
        Assert.IsNull(LyricsDisplayPolicy.SecondaryPresentation(line, "zh-CN", true));
        Assert.AreEqual("Translation", LyricsDisplayPolicy.SecondaryPresentation(line, "en-US", true, showAiLabel: false));
        Assert.AreEqual(LyricsTranslationOrigin.LocalAi, line.TranslationOrigin);
        Assert.AreEqual("en-US", line.TranslationLanguage);
        Assert.IsNull(LyricsDisplayPolicy.SecondaryPresentation(line, "zh-CN", true, showAiLabel: false));
    }

    [TestMethod]
    public void RenderedTranslationMustIntersectTheScrollViewport()
    {
        Assert.IsTrue(LyricsDisplayPolicy.IntersectsViewport(0, 20, 160, 28, 180, 80));
        Assert.IsTrue(LyricsDisplayPolicy.IntersectsViewport(-50, -20, 160, 28, 180, 80));
        Assert.IsFalse(LyricsDisplayPolicy.IntersectsViewport(0, 80, 160, 28, 180, 80));
        Assert.IsFalse(LyricsDisplayPolicy.IntersectsViewport(0, -28, 160, 28, 180, 80));
        Assert.IsFalse(LyricsDisplayPolicy.IntersectsViewport(180, 20, 160, 28, 180, 80));
        Assert.IsFalse(LyricsDisplayPolicy.IntersectsViewport(-160, 20, 160, 28, 180, 80));
        Assert.IsFalse(LyricsDisplayPolicy.IntersectsViewport(0, 20, 160, 28, 0, 80));
        Assert.IsFalse(LyricsDisplayPolicy.IntersectsViewport(double.NaN, 20, 160, 28, 180, 80));
    }

    [TestMethod]
    public void KnownTranslationTargetMustMatchAppLanguage()
    {
        var line = First with { Secondary = "English translation", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "en-US" };
        Assert.AreEqual("English translation", LyricsDisplayPolicy.Secondary(line, "en-GB", true));
        Assert.IsNull(LyricsDisplayPolicy.Secondary(line, "zh-CN", true));
        Assert.IsNull(LyricsDisplayPolicy.Secondary(line with { TranslationLanguage = null }, "en-US", true));
        Assert.IsNull(LyricsDisplayPolicy.Secondary(line with { TranslationOrigin = LyricsTranslationOrigin.Provider }, "zh-CN", true));
        Assert.IsNull(LyricsDisplayPolicy.Secondary(line, "en-US", false));
    }

    [TestMethod]
    public void ShortGapKeepsPreviousLyricInsteadOfTitle()
    {
        var state = LyricsDisplayPolicy.Presentation([First, Next], LyricsHighlightFrame.Empty, TimeSpan.FromSeconds(2.3), 0);
        Assert.IsTrue(state.HasLyrics);
        Assert.AreSame(First, state.Line);
        Assert.IsTrue(state.IsWaiting);
        Assert.IsFalse(state.IsInterlude);
    }

    [TestMethod]
    public void LongGapUsesInterludeWithoutLosingLyricLayout()
    {
        var state = LyricsDisplayPolicy.Presentation([First, Next], LyricsHighlightFrame.Empty, TimeSpan.FromSeconds(5), 0);
        Assert.IsTrue(state.IsInterlude);
        Assert.AreSame(First, state.Line);
        Assert.AreEqual("Translation", state.Line!.Secondary);
    }

    [TestMethod]
    public void NextLineAndSeekingResumeFromTimelineNotPreviousDisplayState()
    {
        var document = new LyricsDocument([First, Next], DropSpace.Core.Models.LyricsProviderKind.LocalLrc);
        var engine = new LyricsTimelineEngine();
        foreach (var position in new[] { 8.5, 1d })
        {
            var time = TimeSpan.FromSeconds(position);
            var state = LyricsDisplayPolicy.Presentation(document.Lines, engine.GetFrame(document, time, 0), time, 0);
            Assert.IsFalse(state.IsWaiting);
            Assert.IsFalse(state.IsInterlude);
            Assert.AreEqual(position > 8 ? "Next" : "First", state.Line!.Text);
        }
    }

    [TestMethod]
    public void IntroOutroAndDelayStayInMatchedLyricsMode()
    {
        var intro = LyricsDisplayPolicy.Presentation([Next], LyricsHighlightFrame.Empty, TimeSpan.FromSeconds(4), 0);
        Assert.IsTrue(intro.HasLyrics && intro.IsInterlude);
        var outro = LyricsDisplayPolicy.Presentation([First, Next], LyricsHighlightFrame.Empty, TimeSpan.FromSeconds(14), 0);
        Assert.IsTrue(outro.HasLyrics && outro.IsInterlude);
        Assert.AreSame(Next, outro.Line);
        var delayed = LyricsDisplayPolicy.Presentation([First, Next], LyricsHighlightFrame.Empty, TimeSpan.FromSeconds(4), 1000);
        Assert.IsTrue(delayed.IsInterlude);
        Assert.IsFalse(LyricsDisplayPolicy.Presentation([], LyricsHighlightFrame.Empty, TimeSpan.FromSeconds(4), 0).HasLyrics);
    }

    [TestMethod]
    public void EnglishModeHidesChineseTranslationWithoutChangingOriginal()
    {
        var line = new LyricsLine(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Original", "中文翻译", []);
        Assert.IsNull(LyricsDisplayPolicy.Secondary(line, "en-US", true));
        Assert.AreEqual("中文翻译", LyricsDisplayPolicy.Secondary(line, "zh-CN", true));
        Assert.AreEqual("Original", line.Text);
        Assert.AreEqual("English translation", LyricsDisplayPolicy.Secondary(line with { Secondary = "English translation" }, "en-US", true));
        Assert.IsNull(LyricsDisplayPolicy.Secondary(line, "zh-CN", false));
    }
}
