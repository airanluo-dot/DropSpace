using System.Text.Json.Nodes;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Content;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PreviewCacheFaultTests
{
    private readonly AppStoragePaths _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-preview-faults", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [TestMethod]
    public async Task UnsafeOptionalCacheCannotHideValidPreviewOrTouchLinkTarget()
    {
        var target = Path.Combine(_paths.Root, "external");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.Previews)!);
        var sentinel = Path.Combine(target, "valuable.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        try { Directory.CreateSymbolicLink(_paths.Previews, target); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive("Directory link creation is unavailable in this environment.");
            return;
        }

        try
        {
            var descriptor = await Registry(new FilePreviewCache(_paths)).LoadAsync(Request());
            Assert.AreEqual(PreviewKind.Text, descriptor.Kind);
            Assert.AreEqual("source content", descriptor.Text);
            Assert.AreEqual("preserve", await File.ReadAllTextAsync(sentinel));
            Assert.HasCount(1, Directory.GetFiles(target));
        }
        finally { Directory.Delete(_paths.Previews); }
    }

    [TestMethod]
    public async Task CorruptCachedMetadataIsDiscardedAndRegeneratedFromSource()
    {
        var cache = new FilePreviewCache(_paths);
        var request = Request();
        var registry = Registry(cache);
        await registry.LoadAsync(request);
        var file = Directory.GetFiles(_paths.Previews).Single();
        var damaged = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        damaged["metadata"] = null;
        await File.WriteAllTextAsync(file, damaged.ToJsonString());

        Assert.IsNull(await cache.TryGetAsync(request.Item.Id, request.Item.Revision, PreviewKind.Text,
            request.Page, request.TargetPixelWidth), "A missing required metadata collection must be a cache miss.");
        Assert.IsFalse(File.Exists(file), "Corrupt cache entries must not be repeatedly offered to the UI.");
        var regenerated = await registry.LoadAsync(request);
        Assert.AreEqual("source content", regenerated.Text);
        Assert.IsNotNull(regenerated.Metadata);
        Assert.IsTrue(regenerated.Metadata.ContainsKey("byteLength"));
    }

    private PreviewProviderRegistry Registry(IPreviewCache cache) => new(
        [new TextPreviewProvider(new ItemContentResolver(_paths))], cache,
        NullLogger<PreviewProviderRegistry>.Instance);

    private static PreviewRequest Request() => new(new(Guid.NewGuid(), ItemKind.Text, ItemStatus.Available,
        "preview", null, null, null, "text/plain", "source content", null, 1));
}
