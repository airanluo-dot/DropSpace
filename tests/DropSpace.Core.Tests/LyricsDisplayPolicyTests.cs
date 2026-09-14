using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsDisplayPolicyTests
{
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
