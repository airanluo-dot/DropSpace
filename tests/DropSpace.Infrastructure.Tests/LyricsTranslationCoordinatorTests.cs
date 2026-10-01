using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

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
}
