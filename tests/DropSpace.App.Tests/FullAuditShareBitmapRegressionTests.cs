using DropSpace.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class FullAuditShareBitmapRegressionTests
{
    [TestMethod]
    public async Task EncodedBitmapKeepsItsDecoderFormatAndOriginalBytesAsync()
    {
        foreach (var encoderId in new[]
                 {
                     BitmapEncoder.JpegEncoderId,
                     BitmapEncoder.BmpEncoderId,
                     BitmapEncoder.PngEncoderId,
                 })
        {
            var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var bytes = await EncodePixelAsync(encoderId);
                var path = Path.Combine(root, "shared-image.png");
                await File.WriteAllBytesAsync(path, bytes);

                var validated = await ShareTargetActivationService.ValidateSharedBitmapFileAsync(
                    path, 1024 * 1024, 1024);

                Assert.IsTrue(WindowsImageCodecPreflight.CanDecode(validated, Path.GetExtension(validated), null));
                CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(validated));
                Assert.AreEqual(1, Directory.EnumerateFiles(root).Count());
                if (encoderId != BitmapEncoder.PngEncoderId)
                {
                    Assert.AreNotEqual(".png", Path.GetExtension(validated));
                    Assert.IsFalse(File.Exists(path));
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CancelledBitmapValidationLeavesCallerOwnedFileForCleanupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "shared-image.png");
            var bytes = await EncodePixelAsync(BitmapEncoder.JpegEncoderId);
            await File.WriteAllBytesAsync(path, bytes);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                ShareTargetActivationService.ValidateSharedBitmapFileAsync(
                    path, 1024 * 1024, 1024, cancellation.Token));

            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            Assert.AreEqual(1, Directory.EnumerateFiles(root).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<byte[]> EncodePixelAsync(Guid encoderId)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, 1, 1, 96, 96,
            [32, 64, 128, 255]);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var reader = new DataReader(stream);
        var length = checked((uint)stream.Size);
        Assert.AreEqual(length, await reader.LoadAsync(length));
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
