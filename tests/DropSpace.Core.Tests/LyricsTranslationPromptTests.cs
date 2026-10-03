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
    public void OutputSchemaRequiresEveryRequestedIdExactlyOnceInOrder()
    {
        using var schema = System.Text.Json.JsonDocument.Parse(LyricsTranslationPrompt.OutputSchema([12, 13, 16]));
        Assert.AreEqual(3, schema.RootElement.GetProperty("minItems").GetInt32());
        Assert.AreEqual(3, schema.RootElement.GetProperty("maxItems").GetInt32());
        var tuples = schema.RootElement.GetProperty("prefixItems").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { 12, 13, 16 }, tuples.Select(item => item.GetProperty("properties").GetProperty("id").GetProperty("const").GetInt32()).ToArray());
        Assert.IsTrue(tuples.All(item => item.GetProperty("properties").GetProperty("text").GetProperty("minLength").GetInt32() == 1));
        Assert.Throws<ArgumentException>(() => LyricsTranslationPrompt.OutputSchema([1, 1]));
        Assert.Throws<ArgumentException>(() => LyricsTranslationPrompt.OutputSchema([]));
    }

    [TestMethod]
    public void ContextDoesNotExposeOutputIdsOutsideTheRequestedBatch()
    {
        var document = new LyricsDocument(Enumerable.Range(0, 48).Select(index =>
            new LyricsLine(TimeSpan.FromSeconds(index), TimeSpan.FromSeconds(index + 1), $"line {index}", null, [])).ToArray(), LyricsProviderKind.LocalLrc);
        var prompt = LyricsTranslationPrompt.Build(Query, document, [12, 13], "en");
        var dataStart = prompt.IndexOf("SOURCE DATA JSON:", StringComparison.Ordinal) + "SOURCE DATA JSON:".Length;
        var dataEnd = prompt.IndexOf("Translate only lines.", dataStart, StringComparison.Ordinal);
        using var json = System.Text.Json.JsonDocument.Parse(prompt[dataStart..dataEnd]);
        CollectionAssert.AreEqual(new[] { 12, 13 }, json.RootElement.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("id").GetInt32()).ToArray());
        Assert.IsTrue(json.RootElement.GetProperty("background").EnumerateArray().All(line => line.ValueKind == System.Text.Json.JsonValueKind.String));
        Assert.IsFalse(json.RootElement.GetProperty("background").EnumerateArray().Any(line => line.GetString() is "line 12" or "line 13"));
    }

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
