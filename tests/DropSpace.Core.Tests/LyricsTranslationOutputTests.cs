using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsTranslationOutputTests
{
    private static LyricsDocument Source => new([
        new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Same", null, [new("Same", TimeSpan.Zero, TimeSpan.FromSeconds(2))]),
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), "Same", null, [])], LyricsProviderKind.LocalLrc);

    [TestMethod]
    public void RepeatedLinesRetainSeparateIdsAndTimings()
    {
        var source = Source;
        Assert.IsTrue(LyricsTranslationOutput.TryApply("[{\"id\":0,\"text\":\"相同\"},{\"id\":1,\"text\":\"相同\"}]", source, [0, 1], "zh-CN", out var result));
        for (var index = 0; index < 2; index++)
        {
            Assert.AreEqual(source.Lines[index].Text, result.Lines[index].Text);
            Assert.AreEqual(source.Lines[index].Start, result.Lines[index].Start);
            Assert.AreEqual(source.Lines[index].End, result.Lines[index].End);
            Assert.AreSame(source.Lines[index].Words, result.Lines[index].Words);
            Assert.AreEqual(LyricsTranslationOrigin.LocalAi, result.Lines[index].TranslationOrigin);
        }
        Assert.IsNull(source.Lines[0].Secondary);
    }

    [TestMethod]
    [DataRow("译文�{translateIds:[0,1]}")]
    [DataRow("译文[end of text]")]
    [DataRow("<|im_start|>assistant")]
    public void RuntimeAndPromptFragmentsAreRejectedEvenInsideValidJson(string text)
    {
        var source = Source;
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { new { id = 0, text } });
        Assert.IsFalse(LyricsTranslationOutput.TryApply(json, source, [0], "zh-CN", out var result));
        Assert.AreSame(source, result);
    }

    [TestMethod]
    [DataRow("[{\"id\":0,\"text\":\"a\"},{\"id\":0,\"text\":\"b\"}]")]
    [DataRow("[{\"id\":1,\"text\":\"a\"},{\"id\":0,\"text\":\"b\"}]")]
    [DataRow("[{\"id\":0,\"text\":\"a\"}]")]
    [DataRow("[{\"id\":0,\"id\":0,\"text\":\"a\"},{\"id\":1,\"text\":\"b\"}]")]
    [DataRow("[{\"id\":0,\"text\":\"a\",\"time\":0},{\"id\":1,\"text\":\"b\"}]")]
    [DataRow("[{\"id\":0,\"text\":\"\"},{\"id\":1,\"text\":\"b\"}]")]
    [DataRow("Explanation: []")]
    public void InvalidOutputIsRejectedAtomically(string json)
    {
        var source = Source;
        Assert.IsFalse(LyricsTranslationOutput.TryApply(json, source, [0, 1], "en-US", out var result));
        Assert.AreSame(source, result);
    }
}
