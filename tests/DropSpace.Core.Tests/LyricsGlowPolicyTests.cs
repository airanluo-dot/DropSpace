using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsGlowPolicyTests
{
    [TestMethod]
    public void OnlyActuallyVisibleAiTranslationQualifiesInMiddleMode()
    {
        foreach (var origin in Enum.GetValues<LyricsTranslationOrigin>())
        foreach (var playing in new[] { false, true })
        foreach (var islandVisible in new[] { false, true })
        foreach (var textVisible in new[] { false, true })
        {
            Assert.AreEqual(playing && islandVisible && textVisible && origin == LyricsTranslationOrigin.LocalAi,
                LyricsGlowPolicy.IsEligible(LyricsGlowMode.AiLyrics, playing, islandVisible, textVisible, origin, "Translated"));
        }
    }

    [TestMethod]
    public void MusicModeDoesNotDependOnLyricsOrInterlude()
    {
        Assert.IsTrue(LyricsGlowPolicy.IsEligible(LyricsGlowMode.Music, true, true, false, LyricsTranslationOrigin.None, null));
        Assert.IsFalse(LyricsGlowPolicy.IsEligible(LyricsGlowMode.Music, false, true, true, LyricsTranslationOrigin.LocalAi, "Text"));
        Assert.IsFalse(LyricsGlowPolicy.IsEligible(LyricsGlowMode.Music, true, false, true, LyricsTranslationOrigin.LocalAi, "Text"));
    }

    [TestMethod]
    public void EmptyAiTextAndOffNeverQualify()
    {
        foreach (var text in new string?[] { null, "", "  " })
            Assert.IsFalse(LyricsGlowPolicy.IsEligible(LyricsGlowMode.AiLyrics, true, true, true, LyricsTranslationOrigin.LocalAi, text));
        Assert.IsFalse(LyricsGlowPolicy.IsEligible(LyricsGlowMode.Off, true, true, true, LyricsTranslationOrigin.LocalAi, "Text"));
        Assert.IsFalse(LyricsGlowPolicy.IsEligible((LyricsGlowMode)99, true, true, true, LyricsTranslationOrigin.LocalAi, "Text"));
    }

    [TestMethod]
    public void DefaultsNeverSelectMusicMode()
    {
        Assert.AreEqual(LyricsGlowMode.Off, LyricsGlowPolicy.OnAiEnabledChanged(false));
        Assert.AreEqual(LyricsGlowMode.AiLyrics, LyricsGlowPolicy.OnAiEnabledChanged(true));
    }
}
