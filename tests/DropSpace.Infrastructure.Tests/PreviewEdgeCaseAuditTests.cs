using System.Buffers.Binary;
using System.Text;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Content;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PreviewEdgeCaseAuditTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-preview-audit", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task InvalidBmpHeightUsesFallbackInsteadOfEscapingWithAnOverflowException()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "invalid.bmp");
        var bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), int.MinValue);
        await File.WriteAllBytesAsync(path, bytes);
        var paths = new AppStoragePaths(Path.Combine(_root, "app"));
        var registry = new PreviewProviderRegistry(
            [new ImagePreviewProvider(new ItemContentResolver(paths)), new UnknownPreviewProvider()],
            new FilePreviewCache(paths), NullLogger<PreviewProviderRegistry>.Instance);

        var descriptor = await registry.LoadAsync(new PreviewRequest(FileSnapshot(path, ".bmp", bytes.Length)));

        Assert.AreEqual(PreviewKind.Unknown, descriptor.Kind);
    }

    [TestMethod]
    public async Task ValidTopDownBmpPreservesItsPositivePreviewDimensions()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "top-down.bmp");
        var bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -3);
        await File.WriteAllBytesAsync(path, bytes);
        var provider = new ImagePreviewProvider(new ItemContentResolver(new AppStoragePaths(_root)));

        var capability = await provider.ProbeAsync(FileSnapshot(path, ".bmp", bytes.Length));

        Assert.IsTrue(capability.CanPreview);
        Assert.AreEqual(2, capability.PixelWidth);
        Assert.AreEqual(3, capability.PixelHeight);
    }

    [TestMethod]
    public async Task ValidCoreHeaderBmpUsesItsActualDimensionsInsteadOfExceedingThePixelBudget()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "core-header.bmp");
        // Complete one-pixel, 24-bit OS/2 1.x BMP: 14-byte file header,
        // 12-byte BITMAPCOREHEADER and one four-byte aligned BGR scanline.
        var bytes = new byte[30];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 26);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), 24);
        bytes[28] = 255;
        await File.WriteAllBytesAsync(path, bytes);
        var provider = new ImagePreviewProvider(new ItemContentResolver(new AppStoragePaths(_root)));

        var capability = await provider.ProbeAsync(FileSnapshot(path, ".bmp", bytes.Length));

        Assert.IsTrue(capability.CanPreview);
        Assert.AreEqual(1, capability.PixelWidth);
        Assert.AreEqual(1, capability.PixelHeight);
    }

    [TestMethod]
    public async Task PdfGrowthAfterCaptureDoesNotTurnAReadableExternalPdfIntoAnUnknownPreview()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "document.pdf");
        var original = Encoding.ASCII.GetBytes("%PDF-1.7\noriginal document\n%%EOF\n");
        await File.WriteAllBytesAsync(path, original);
        var captured = FileSnapshot(path, ".pdf", original.Length);
        await File.AppendAllTextAsync(path, "% external save added metadata\n");
        var expectedBytes = await File.ReadAllBytesAsync(path);
        var paths = new AppStoragePaths(Path.Combine(_root, "app"));
        var registry = new PreviewProviderRegistry(
            [new PdfPreviewProvider(new ItemContentResolver(paths)), new UnknownPreviewProvider()],
            new FilePreviewCache(paths), NullLogger<PreviewProviderRegistry>.Instance);

        var descriptor = await registry.LoadAsync(new PreviewRequest(captured));

        Assert.AreEqual(PreviewKind.Pdf, descriptor.Kind);
        CollectionAssert.AreEqual(expectedBytes, descriptor.Bytes);
    }

    private static DropItemSnapshot FileSnapshot(string path, string extension, long knownBytes) =>
        new(Guid.NewGuid(), ItemKind.File, ItemStatus.Available, Path.GetFileName(path),
            path, extension, knownBytes, null, null, null, 1);
}
