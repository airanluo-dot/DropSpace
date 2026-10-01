using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using System.Text.Json;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsTranslationCoordinatorTests
{
    private const string ModelHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly LyricsQuery Query = new("Test", "Test", "", TimeSpan.FromSeconds(3));
    private static readonly LyricsDocument Source = new([new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "Hello", null, [])], LyricsProviderKind.LocalLrc);

    [TestMethod]
    public async Task CacheHitDoesNotRunInferenceAndLanguageHasSeparateKey()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var service = new LyricsTranslationCoordinator(new(root));
            var count = 0;
            Task<string> Infer(string prompt, CancellationToken token) { count++; return Task.FromResult("[{\"id\":0,\"text\":\"你好\"}]"); }
            var first = await service.TranslateAsync(Query, Source, "zh-CN", ModelHash, Infer, CancellationToken.None);
            var cached = await service.TranslateAsync(Query, Source, "zh-CN", ModelHash, Infer, CancellationToken.None);
            Assert.AreEqual("你好", first.Lines[0].Secondary);
            Assert.AreEqual("你好", cached.Lines[0].Secondary);
            Assert.AreEqual(1, count);
            await service.TranslateAsync(Query, Source, "en-US", ModelHash, Infer, CancellationToken.None);
            Assert.AreEqual(2, count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InvalidOutputRetriesOnlyOnceAndKeepsOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new LyricsTranslationCoordinator(new(root));
        var count = 0;
        var result = await service.TranslateAsync(Query, Source, "zh-CN", ModelHash,
            (prompt, token) => { count++; return Task.FromResult("invalid"); }, CancellationToken.None);
        Assert.AreSame(Source, result);
        Assert.AreEqual(2, count);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task CancelledResultCannotBeCachedOrReturned()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var cancelled = new CancellationTokenSource();
        var service = new LyricsTranslationCoordinator(new(root));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.TranslateAsync(Query, Source, "zh-CN", ModelHash,
            (prompt, token) => { cancelled.Cancel(); return Task.FromResult("[{\"id\":0,\"text\":\"你好\"}]"); }, cancelled.Token));
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task CompleteTranslationLargerThanCacheBudgetIsStillReturned()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var source = Source with { Lines = Enumerable.Range(0, 100).Select(index =>
                new LyricsLine(TimeSpan.FromSeconds(index), TimeSpan.FromSeconds(index + 1), "Original " + index, null, [])).ToArray() };
            var translation = string.Concat(Enumerable.Repeat("Translated line. ", 50));
            var calls = 0;
            var result = await new LyricsTranslationCoordinator(new(root)).TranslateAsync(Query, source, "en-US", ModelHash,
                (prompt, token) => { calls++; return Task.FromResult(OutputForPrompt(prompt, translation)); }, CancellationToken.None);

            Assert.AreEqual(9, calls);
            Assert.IsTrue(result.Lines.All(line => line.Secondary == translation));
            Assert.IsFalse(Directory.Exists(root));
            AssertOriginalsUnchanged(source, result);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MatchingProviderLinesSurviveBothInferenceAndCacheHit()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var provider = Source.Lines[0] with
            {
                Secondary = "Provider English", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "en-GB",
            };
            var source = Source with { Lines = [provider, Source.Lines[0] with { Text = "Second" }, Source.Lines[0] with
            {
                Text = "Third", Secondary = "其他译文", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-CN",
            }] };
            var calls = 0;
            var coordinator = new LyricsTranslationCoordinator(new(root));
            Task<string> Infer(string prompt, CancellationToken token)
            {
                calls++;
                CollectionAssert.AreEqual(new[] { 1, 2 }, RequestedIds(prompt));
                return Task.FromResult(OutputForPrompt(prompt, "Local English"));
            }
            foreach (var result in new[]
            {
                await coordinator.TranslateAsync(Query, source, "en-US", ModelHash, Infer, CancellationToken.None),
                await coordinator.TranslateAsync(Query, source, "en-US", ModelHash, Infer, CancellationToken.None),
            })
            {
                Assert.AreSame(provider, result.Lines[0]);
                Assert.AreEqual("Local English", result.Lines[1].Secondary);
                Assert.AreEqual("Local English", result.Lines[2].Secondary);
                AssertOriginalsUnchanged(source, result);
            }
            Assert.AreEqual(1, calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FullyCoveredProviderSongDoesNotInferOrCache()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Source with { Lines = [Source.Lines[0] with
        {
            Secondary = "你好", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-Hans",
        }] };
        var result = await new LyricsTranslationCoordinator(new(root)).TranslateAsync(Query, source, "zh-CN", ModelHash,
            (prompt, token) => throw new InvalidOperationException("No inference should be needed."), CancellationToken.None);
        Assert.AreSame(source, result);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    [DataRow("Bonjour", "en-US")]
    [DataRow("東京", "zh-CN")]
    public async Task UnknownProviderLanguageNeverSuppressesInference(string providerText, string target)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var source = Source with { Lines = [Source.Lines[0] with
            {
                Secondary = providerText, TranslationOrigin = LyricsTranslationOrigin.Provider,
            }] };
            var calls = 0;
            var result = await new LyricsTranslationCoordinator(new(root)).TranslateAsync(Query, source, target, ModelHash,
                (prompt, token) => { calls++; return Task.FromResult(OutputForPrompt(prompt, "Translated")); }, CancellationToken.None);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(LyricsTranslationOrigin.LocalAi, result.Lines[0].TranslationOrigin);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LaterBatchFailureRetainsEntireOriginalIncludingProviderTranslation()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Source with { Lines = Enumerable.Range(0, 14).Select(index => Source.Lines[0] with { Text = "Original " + index }).ToArray() };
        source = source with { Lines = [source.Lines[0] with
        {
            Secondary = "Provider English", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "en-US",
        }, .. source.Lines.Skip(1)] };
        var calls = 0;
        var result = await new LyricsTranslationCoordinator(new(root)).TranslateAsync(Query, source, "en-US", ModelHash,
            (prompt, token) => Task.FromResult(++calls == 1 ? OutputForPrompt(prompt, "Translated") : "invalid"), CancellationToken.None);
        Assert.AreSame(source, result);
        Assert.AreEqual(3, calls);
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public async Task ActualTokenizerBudgetSplitsWithoutDroppingRequestedLines()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var source = Source with { Lines = Enumerable.Range(0, 13).Select(index => Source.Lines[0] with { Text = "Original " + index }).ToArray() };
            var seen = new List<int>();
            var result = await new LyricsTranslationCoordinator(new(root)).TranslateBatchesAsync(Query, source, "zh-CN", ModelHash,
                (prompt, ids, token) =>
                {
                    Assert.IsTrue(ids.Count <= 3);
                    seen.AddRange(ids);
                    return Task.FromResult(OutputForPrompt(prompt, "翻译"));
                }, CancellationToken.None,
                (prompt, token) => Task.FromResult(RequestedIds(prompt).Length * 600));
            CollectionAssert.AreEqual(Enumerable.Range(0, 13).ToArray(), seen.ToArray());
            AssertOriginalsUnchanged(source, result);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task OversizedSingleLineFailsBeforeInferenceAndCannotCache()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new LyricsTranslationCoordinator(new(root)).TranslateBatchesAsync(Query, Source, "zh-CN", ModelHash,
                (_, _, _) => throw new AssertFailedException("Must not infer an oversized prompt."), CancellationToken.None,
                (_, _) => Task.FromResult(LyricsTranslationPrompt.MaximumPromptTokens + 1)));
        Assert.IsFalse(Directory.Exists(root));
    }

    private static int[] RequestedIds(string prompt)
    {
        using var data = JsonDocument.Parse(prompt.Split("SOURCE DATA JSON:", StringSplitOptions.None)[1].Split("Translate only lines.", StringSplitOptions.None)[0]);
        return data.RootElement.GetProperty("lines").EnumerateArray().Select(value => value.GetProperty("id").GetInt32()).ToArray();
    }

    private static string OutputForPrompt(string prompt, string text) =>
        JsonSerializer.Serialize(RequestedIds(prompt).Select(id => new { id, text }));

    private static void AssertOriginalsUnchanged(LyricsDocument source, LyricsDocument result)
    {
        for (var index = 0; index < source.Lines.Count; index++)
        {
            Assert.AreEqual(source.Lines[index].Text, result.Lines[index].Text);
            Assert.AreEqual(source.Lines[index].Start, result.Lines[index].Start);
            Assert.AreEqual(source.Lines[index].End, result.Lines[index].End);
            Assert.AreSame(source.Lines[index].Words, result.Lines[index].Words);
        }
    }
}
