using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class UnifiedLyricsCacheRegressionTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(30), "track");
    private static LyricsDocument Document(string text = "Original") => new LyricsDocument(
        [new(TimeSpan.Zero, TimeSpan.FromSeconds(30), text, "普通译文", [])
        { TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-CN" }], LyricsProviderKind.NetEase)
        .Bind(Query, Query.Title, Query.Artist, Query.Album, 30, 10, "candidate");

    [TestMethod]
    public async Task ClearFencesOtherInstancesSharingTheSameCanonicalRoot()
    {
        var root = Root();
        try
        {
            var writer = new AiLyricsCache(new LyricsCache(root + Path.DirectorySeparatorChar));
            var generation = writer.Generation;
            await new LyricsCache(root).ClearAsync(default);
            await writer.WriteAsync(Key, "[]", generation, default);
            Assert.IsNull(await writer.ReadAsync(Key, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("null-line")]
    [DataRow("null-words")]
    [DataRow("null-word")]
    [DataRow("null-text")]
    [DataRow("reversed-time")]
    public async Task StructurallyCorruptDocumentIsACacheMiss(string corruption)
    {
        var root = Root();
        try
        {
            var cache = new LyricsCache(root);
            await cache.WriteDocumentAsync("song", Document(), cache.Generation, default);
            var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Directory.GetFiles(root).Single()))!;
            var line = node["lines"]![0]!;
            switch (corruption)
            {
                case "null-line": node["lines"]![0] = null; break;
                case "null-words": line["words"] = null; break;
                case "null-word": line["words"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null); break;
                case "null-text": line["text"] = null; break;
                case "reversed-time": line["end"] = "-00:00:01"; break;
            }
            await File.WriteAllTextAsync(Directory.GetFiles(root).Single(), node.ToJsonString());
            Assert.IsNull(await cache.ReadDocumentAsync("song", default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task OversizedOptionalPersistenceDoesNotDiscardProviderResult()
    {
        var root = Root();
        try
        {
            var provider = new Provider(_ => Task.FromResult(Document(new string('中', 800_000))));
            var service = new LyricsService(new([provider]), new LyricsCache(root));
            var result = await service.QueryDetailedAsync(Query, new() { Enabled = true }, default);
            Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
            Assert.AreEqual(800_000, result.Document.Lines[0].Text.Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProviderFinishingAfterClearCannotRepopulateDisk()
    {
        var root = Root();
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cache = new LyricsCache(root);
            var provider = new Provider(_ => { entered.SetResult(); return finish.Task; });
            var request = new LyricsService(new([provider]), cache).QueryDetailedAsync(Query, new() { Enabled = true }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cache.ClearAsync(default);
            finish.SetResult(Document());
            Assert.AreEqual(LyricsQueryStatus.Found, (await request).Status);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RestartPreservesOriginalAndProviderTranslationWithoutSearch()
    {
        var root = Root();
        try
        {
            var provider = new Provider(_ => Task.FromResult(Document()));
            await new LyricsService(new([provider]), new LyricsCache(root)).QueryDetailedAsync(Query, new() { Enabled = true }, default);
            var result = await new LyricsService(new([provider]), new LyricsCache(root)).QueryDetailedAsync(Query, new() { Enabled = true }, default);
            Assert.AreEqual(1, provider.Calls);
            Assert.AreEqual("Original", result.Document.Lines[0].Text);
            Assert.AreEqual("普通译文", result.Document.Lines[0].Secondary);
            Assert.AreEqual("zh-CN", result.Document.Lines[0].TranslationLanguage);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RestartReusesAiAndChangedLocalSourceInvalidatesIt()
    {
        var root = Root();
        try
        {
            var source = Document() with { Provider = LyricsProviderKind.LocalLrc, Lines = [new(TimeSpan.Zero, TimeSpan.FromSeconds(30), "Original", null, [])] };
            var calls = 0;
            Task<string> Infer(string _, CancellationToken token) { calls++; return Task.FromResult("[{\"id\":0,\"text\":\"译文\"}]"); }
            await new LyricsTranslationCoordinator(new AiLyricsCache(root)).TranslateAsync(Query, source, "zh-CN", Key, Infer, default);
            var restarted = new LyricsTranslationCoordinator(new AiLyricsCache(root));
            await restarted.TranslateAsync(Query, source, "zh-CN", Key, Infer, default);
            Assert.AreEqual(1, calls);
            await restarted.TranslateAsync(Query, source with { Lines = [source.Lines[0] with { Text = "Edited" }] }, "zh-CN", Key, Infer, default);
            Assert.AreEqual(2, calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LocalLrcEditsAreReadAndClearPreservesUserFilesAndModels()
    {
        var root = Root();
        try
        {
            var cacheRoot = Path.Combine(root, "cache");
            var lrc = Path.Combine(root, "Song.lrc");
            var model = Path.Combine(root, "model.gguf");
            await File.WriteAllTextAsync(lrc, "[ti:Song]\n[ar:Artist]\n[00:00.00]Before");
            await File.WriteAllTextAsync(model, "model");
            var cache = new LyricsCache(cacheRoot);
            var service = new LyricsService(new([new LocalLrcLyricsProvider(() => root)]), cache);
            var settings = new LyricsSettings { Enabled = true, Mode = LyricsMode.LocalLrc };
            Assert.AreEqual("Before", (await service.QueryAsync(Query, settings, default)).Lines[0].Text);
            await File.WriteAllTextAsync(lrc, "[ti:Song]\n[ar:Artist]\n[00:00.00]After");
            Assert.AreEqual("After", (await service.QueryAsync(Query, settings, default)).Lines[0].Text);
            await new AiLyricsCache(cache).WriteAsync(Key, "[]", default);
            await cache.ClearAsync(default);
            Assert.IsTrue((await File.ReadAllTextAsync(lrc)).Contains("After", StringComparison.Ordinal));
            Assert.AreEqual("model", await File.ReadAllTextAsync(model));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ChangedRuntimeSessionIdStillUsesPersistedSource()
    {
        var root = Root();
        try
        {
            var first = DropSpace.Core.Media.MediaSessionSnapshot.Empty with
            {
                SessionId = "first-process-guid", SourceAppUserModelId = "player", TrackTitle = Query.Title,
                Artist = Query.Artist, AlbumTitle = Query.Album,
                Timeline = DropSpace.Core.Media.MediaSessionSnapshot.Empty.Timeline with { End = Query.Duration },
            };
            var restart = first with { SessionId = "new-process-guid" };
            Assert.AreNotEqual(first.TrackIdentity, restart.TrackIdentity);
            Assert.AreEqual(first.LyricsCacheIdentity, restart.LyricsCacheIdentity);
            var provider = new Provider(_ => Task.FromResult(Document() with { Match = Document().Match! with { TrackIdentity = "" } }));
            await new LyricsService(new([provider]), new LyricsCache(root)).QueryDetailedAsync(Query with { TrackIdentity = first.LyricsCacheIdentity }, new() { Enabled = true }, default);
            var result = await new LyricsService(new([provider]), new LyricsCache(root)).QueryDetailedAsync(Query with { TrackIdentity = restart.LyricsCacheIdentity }, new() { Enabled = true }, default);
            Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
            Assert.AreEqual(1, provider.Calls);
            Assert.AreNotEqual(first.LyricsCacheIdentity, (first with { TrackNumber = 2 }).LyricsCacheIdentity);
            Assert.AreNotEqual(first.LyricsCacheIdentity, (first with { SourceAppUserModelId = "other-player" }).LyricsCacheIdentity);
            Assert.AreNotEqual(first.LyricsCacheIdentity, (first with { Timeline = first.Timeline with { End = first.Timeline.End + TimeSpan.FromTicks(1) } }).LyricsCacheIdentity);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LruReadProtectsEntryAndAbandonedOwnedTemporaryFilesAreRemoved()
    {
        var root = Root();
        try
        {
            var cache = new LyricsCache(root);
            await new AiLyricsCache(cache).WriteAsync(Key, "[]", default);
            var recent = Directory.GetFiles(root).Single();
            File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-3));
            var oldest = Path.Combine(root, "source-" + new string('B', 64) + ".lyrics-cache");
            var newer = Path.Combine(root, "source-" + new string('C', 64) + ".lyrics-cache");
            foreach (var path in new[] { oldest, newer })
            {
                using var file = File.Create(path);
                file.SetLength(50L * 1024 * 1024);
            }
            File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));
            var abandoned = recent + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(abandoned, "partial");
            var unrelated = Path.Combine(root, "personal.tmp");
            await File.WriteAllTextAsync(unrelated, "keep");
            Assert.AreEqual("[]", await new AiLyricsCache(cache).ReadAsync(Key, default));
            await cache.SetMaximumBytesAsync(100L * 1024 * 1024);
            Assert.IsFalse(File.Exists(oldest));
            Assert.IsTrue(File.Exists(newer));
            Assert.IsTrue(File.Exists(recent));
            Assert.IsFalse(File.Exists(abandoned));
            Assert.IsTrue(File.Exists(unrelated));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LegacyCleanupDeletesOnlyRecognizedCacheFiles()
    {
        var root = Root();
        try
        {
            var legacy = Path.Combine(root, "AiLyrics", "Cache");
            Directory.CreateDirectory(legacy);
            var owned = Path.Combine(legacy, Key + ".json");
            var temporary = Path.Combine(legacy, Key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            var other = Path.Combine(legacy, "user.json");
            var lrc = Path.Combine(legacy, "Song.lrc");
            var model = Path.Combine(root, "AiLyrics", "Models", "model.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            foreach (var path in new[] { owned, temporary, other, lrc, model }) await File.WriteAllTextAsync(path, "keep unless owned");
            await AiLyricsCache.RemoveLegacyAsync(root, default);
            Assert.IsFalse(File.Exists(owned)); Assert.IsFalse(File.Exists(temporary));
            foreach (var path in new[] { other, lrc, model }) Assert.IsTrue(File.Exists(path));
            await AiLyricsCache.RemoveLegacyAsync(root, default);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LegacyCleanupRejectsRedirectedParentWithoutDeletingExternalEntries()
    {
        var root = Root(); var external = Root();
        try
        {
            var target = Path.Combine(external, "Cache"); Directory.CreateDirectory(target);
            var sentinel = Path.Combine(target, Key + ".json"); await File.WriteAllTextAsync(sentinel, "external");
            try { Directory.CreateSymbolicLink(Path.Combine(root, "AiLyrics"), external); }
            catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            { Assert.Inconclusive("Symbolic-link creation is unavailable: " + error.GetType().Name); }
            await Assert.ThrowsAsync<InvalidDataException>(() => AiLyricsCache.RemoveLegacyAsync(root, default));
            Assert.AreEqual("external", await File.ReadAllTextAsync(sentinel));
        }
        finally { Directory.Delete(root, true); Directory.Delete(external, true); }
    }

    [TestMethod]
    public async Task RawMetadataWithSeparatorCharactersDoesNotAliasAnotherSong()
    {
        var root = Root();
        try
        {
            var provider = new Provider(_ => Task.FromResult(Document()));
            var service = new LyricsService(new([provider]), new LyricsCache(root));
            var query = Query with { TrackIdentity = "" };
            await service.QueryDetailedAsync(query, new() { Enabled = true }, default);
            await service.QueryDetailedAsync(query with { Title = "Song|" }, new() { Enabled = true }, default);
            Assert.AreEqual(2, provider.Calls, "Exact raw metadata must remain distinct even when matching normalization removes punctuation.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CompatibilityConstructorCachesInMemoryWithoutTemporaryDirectories()
    {
        var legacyTemporary = Path.Combine(Path.GetTempPath(), "DropSpace", "LyricsCache");
        var before = Directory.Exists(legacyTemporary) ? Directory.GetDirectories(legacyTemporary).Length : 0;
        var provider = new Provider(_ => Task.FromResult(Document()));
        var service = new LyricsService(new([provider]));
        await service.QueryDetailedAsync(Query, new() { Enabled = true }, default);
        await service.QueryDetailedAsync(Query, new() { Enabled = true }, default);
        Assert.AreEqual(1, provider.Calls);
        Assert.AreEqual(before, Directory.Exists(legacyTemporary) ? Directory.GetDirectories(legacyTemporary).Length : 0);
        service.ClearCache();
        await service.QueryDetailedAsync(Query, new() { Enabled = true }, default);
        Assert.AreEqual(2, provider.Calls);
    }

    [TestMethod]
    public async Task CancelledClearIsReportedIncompleteAndCanBeRetried()
    {
        var root = Root();
        try
        {
            var cache = new AiLyricsCache(root);
            await cache.WriteAsync(Key, "[]", default);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => cache.ClearAsync(stop.Token));
            Assert.AreEqual("[]", await new AiLyricsCache(root).ReadAsync(Key, default));
            await cache.ClearAsync(default);
            Assert.IsNull(await new AiLyricsCache(root).ReadAsync(Key, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LockedEvictionDeclinesCommitRatherThanExceedingQuota()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows delete-sharing semantics are required.");
        var root = Root();
        try
        {
            var old = Path.Combine(root, "source-" + new string('B', 64) + ".lyrics-cache");
            using (var file = File.Create(old)) file.SetLength(100L * 1024 * 1024);
            var configured = new LyricsCache(root);
            await configured.SetMaximumBytesAsync(100L * 1024 * 1024);
            using (var held = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var writer = new AiLyricsCache(new LyricsCache(root));
                await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(Key, "[]", default));
                Assert.IsNull(await writer.ReadAsync(Key, default));
                CollectionAssert.AreEquivalent(new[] { old }, Directory.GetFiles(root));
                await Assert.ThrowsAsync<IOException>(() => configured.ClearAsync(default));
            }
            await configured.ClearAsync(default);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Root() { var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private sealed class Provider(Func<CancellationToken, Task<LyricsDocument>> query) : ILyricsProvider
    {
        public LyricsProviderKind Kind => LyricsProviderKind.NetEase;
        public int Calls { get; private set; }
        public Task<LyricsDocument> QueryAsync(LyricsQuery _, CancellationToken token) { Calls++; return query(token); }
    }
}
