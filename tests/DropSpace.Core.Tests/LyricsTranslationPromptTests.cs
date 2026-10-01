using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsTranslationPromptTests
{
    private static readonly LyricsQuery Query = new("Title", "Artist", "Album", TimeSpan.FromSeconds(20));
    private static readonly LyricsDocument Document = new([new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "A line", null, [])], LyricsProviderKind.LocalLrc);
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public void PromptTargetsEnglishOrChineseExplicitly()
    {
        StringAssert.Contains(LyricsTranslationPrompt.Build(Query, Document, [0], "en-US"), "into English");
        StringAssert.Contains(LyricsTranslationPrompt.Build(Query, Document, [0], "zh-CN"), "into Simplified Chinese");
    }

    [TestMethod]
    public void CacheSeparatesTargetMetadataAndTimeAxis()
    {
        var original = LyricsTranslationPrompt.CacheKey(Query, Document, "en-US", Hash);
        Assert.AreNotEqual(original, LyricsTranslationPrompt.CacheKey(Query, Document, "zh-CN", Hash));
        Assert.AreNotEqual(original, LyricsTranslationPrompt.CacheKey(Query with { Artist = "Other" }, Document, "en-US", Hash));
        Assert.AreNotEqual(original, LyricsTranslationPrompt.CacheKey(Query, Document with { Lines = [Document.Lines[0] with { End = TimeSpan.FromSeconds(3) }] }, "en-US", Hash));
        Assert.AreEqual(original, LyricsTranslationPrompt.CacheKey(Query, Document, "en-GB", Hash.ToLowerInvariant()));
    }

    [TestMethod]
    public void SourceScriptsRemainReadableInPrompt()
    {
        var source = Document with { Lines = [Document.Lines[0] with { Text = "我不会永远等待。雪が降る。" }] };
        var prompt = LyricsTranslationPrompt.Build(Query, source, [0], "en-US");
        StringAssert.Contains(prompt, "我不会永远等待。雪が降る。");
        Assert.IsFalse(prompt.Contains("\\u6211", StringComparison.OrdinalIgnoreCase));
    }
}
