using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Content;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PreviewCacheTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-preview-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task ExternalFileChangesAreVisibleWithoutDatabaseRevisionChange()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(path, "before");
        var paths = new AppStoragePaths(Path.Combine(_root, "app"));
        var registry = new PreviewProviderRegistry([new TextPreviewProvider(new ItemContentResolver(paths))],
            new FilePreviewCache(paths), NullLogger<PreviewProviderRegistry>.Instance);
        var request = new PreviewRequest(Item() with { Kind = ItemKind.File, OriginalPath = path, Extension = ".txt", Text = null });
        Assert.AreEqual("before", (await registry.LoadAsync(request)).Text);
        await File.WriteAllTextAsync(path, "after");
        Assert.AreEqual("after", (await registry.LoadAsync(request)).Text);
        Assert.IsFalse(Directory.Exists(paths.Previews));
    }

    [TestMethod]
    public async Task FailedCacheReadAndWriteDoNotHideSuccessfulPreview()
    {
        var paths = new AppStoragePaths(_root);
        var registry = new PreviewProviderRegistry([new TextPreviewProvider(new ItemContentResolver(paths))],
            new FailedCache(), NullLogger<PreviewProviderRegistry>.Instance);
        Assert.AreEqual("content", (await registry.LoadAsync(new PreviewRequest(Item()))).Text);
    }

    [TestMethod]
    public async Task CacheBoundsEntriesAndRejectsExpiredOrOversizedFiles()
    {
        var paths = new AppStoragePaths(_root);
        var cache = new FilePreviewCache(paths);
        PreviewRequest request = new(Item());
        for (var i = 0; i <= FilePreviewCache.MaximumEntries; i++)
        {
            request = new PreviewRequest(Item());
            await cache.PutAsync(request, Descriptor(request) with { CacheGeneration = cache.Generation });
        }
        Assert.AreEqual(FilePreviewCache.MaximumEntries, Directory.GetFiles(paths.Previews).Length);
        await cache.ClearAsync();
        await cache.PutAsync(request, Descriptor(request) with { CacheGeneration = cache.Generation });
        var file = Directory.GetFiles(paths.Previews).Single();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - FilePreviewCache.MaximumAge - TimeSpan.FromMinutes(1));
        Assert.IsNull(await GetAsync(cache, request));
        await cache.PutAsync(request, Descriptor(request) with { CacheGeneration = cache.Generation });
        file = Directory.GetFiles(paths.Previews).Single();
        using (var stream = File.OpenWrite(file)) stream.SetLength(FilePreviewCache.MaximumEntryBytes + 1);
        Assert.IsNull(await GetAsync(cache, request));
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public async Task ClearPreventsLateRenderingFromRecreatingDeletedCacheContent()
    {
        var paths = new AppStoragePaths(_root);
        var cache = new FilePreviewCache(paths);
        var request = new PreviewRequest(Item());
        var renderedBeforeClear = Descriptor(request) with { CacheGeneration = cache.Generation };
        await cache.PutAsync(request, renderedBeforeClear);
        await cache.ClearAsync();
        await cache.PutAsync(request, renderedBeforeClear);
        Assert.IsNull(await GetAsync(cache, request));
        Assert.IsFalse(Directory.Exists(paths.Previews));
        await cache.PutAsync(request, Descriptor(request) with { CacheGeneration = cache.Generation });
        Assert.IsNotNull(await GetAsync(cache, request));
    }

    private static DropItemSnapshot Item() => new(Guid.NewGuid(), ItemKind.Text, ItemStatus.Available,
        "test", null, null, null, "text/plain", "content", null, 1);
    private static PreviewDescriptor Descriptor(PreviewRequest request) => new(request.Item.Id, PreviewKind.Text,
        "test", "text/plain", "content", null, null, null, null, null, new Dictionary<string, string>());
    private static Task<PreviewDescriptor?> GetAsync(IPreviewCache cache, PreviewRequest request) =>
        cache.TryGetAsync(request.Item.Id, request.Item.Revision, PreviewKind.Text, request.Page, request.TargetPixelWidth);

    private sealed class FailedCache : IPreviewCache
    {
        public long Generation => 0;
        public Task<PreviewDescriptor?> TryGetAsync(Guid itemId, int revision, PreviewKind kind, int page, int targetPixelWidth, CancellationToken cancellationToken = default) => throw new IOException("cache unavailable");
        public Task PutAsync(PreviewRequest request, PreviewDescriptor descriptor, CancellationToken cancellationToken = default) => throw new IOException("cache unavailable");
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
