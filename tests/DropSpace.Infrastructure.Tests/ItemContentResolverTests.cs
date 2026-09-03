using DropSpace.Core.Content;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Content;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ItemContentResolverTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolverAndPreview_RecognizeExternalImageEvenWhenKindIsFile()
    {
        var path = Path.Combine(_root, "photo.PNG");
        await File.WriteAllBytesAsync(path, OnePixelPng());
        var item = Snapshot(ItemKind.File, path, ".PNG", null, "image/png");
        var resolver = new ItemContentResolver(new AppStoragePaths(_root));

        var content = resolver.Resolve(item);
        var provider = new ImagePreviewProvider(resolver);
        var capability = await provider.ProbeAsync(item);

        Assert.AreEqual(ItemContentType.Image, content.Type);
        Assert.AreEqual(ItemContentSource.ExternalPath, content.Source);
        Assert.IsTrue(content.HasReadablePath);
        Assert.IsTrue(capability.CanPreview);
        Assert.AreEqual(1, capability.PixelWidth);
        Assert.AreEqual(1, capability.PixelHeight);
    }

    [TestMethod]
    public async Task ResolverAndPreview_ReadImageFromControlledAppPayload()
    {
        var paths = new AppStoragePaths(_root);
        var store = new FilePayloadStore(paths);
        await using var source = new MemoryStream(OnePixelPng());
        var payload = await store.WriteFileAsync("images", ".png", source, 1024 * 1024);
        var item = Snapshot(ItemKind.Image, null, null, payload, "image/png");
        var resolver = new ItemContentResolver(paths);
        var provider = new ImagePreviewProvider(resolver);

        var content = resolver.Resolve(item);
        var capability = await provider.ProbeAsync(item);
        var descriptor = await provider.LoadAsync(new PreviewRequest(item));

        Assert.AreEqual(ItemContentType.Image, content.Type);
        Assert.AreEqual(ItemContentSource.AppPayload, content.Source);
        Assert.IsTrue(content.HasReadablePath);
        Assert.IsTrue(capability.CanPreview);
        Assert.IsTrue(descriptor.HasBytes);
        Assert.AreEqual(1, descriptor.PixelWidth);
        Assert.AreEqual(1, descriptor.PixelHeight);
    }

    [TestMethod]
    public void Resolver_FailsClosedForMissingAndEscapingPayloads()
    {
        var paths = new AppStoragePaths(_root);
        var resolver = new ItemContentResolver(paths);
        var missing = new PayloadRecord(Guid.NewGuid(), "images", "images/aa/missing.png", 10, "hash", DateTimeOffset.UtcNow, 1);
        var escaping = missing with { RelativePath = Path.Combine("..", "outside.png") };

        var missingResult = resolver.Resolve(Snapshot(ItemKind.Image, null, null, missing, "image/png"));
        var escapingResult = resolver.Resolve(Snapshot(ItemKind.Image, null, null, escaping, "image/png"));

        Assert.IsFalse(missingResult.IsAvailable);
        Assert.IsFalse(missingResult.HasReadablePath);
        Assert.IsFalse(escapingResult.IsAvailable);
        Assert.IsNull(escapingResult.ReadablePath);
    }

    private static DropItemSnapshot Snapshot(
        ItemKind kind,
        string? originalPath,
        string? extension,
        PayloadRecord? payload,
        string? mimeType) => new(
            Guid.NewGuid(),
            kind,
            ItemStatus.Available,
            "photo",
            originalPath,
            extension,
            payload?.ByteLength,
            mimeType,
            null,
            null,
            1,
            payload);

    private static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
