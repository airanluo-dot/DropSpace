using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsDisplayPolicyTests
{
    private static readonly LyricsLine First = new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "First", "Translation", []);
    private static readonly LyricsLine Next = new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10), "Next", null, []);

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
