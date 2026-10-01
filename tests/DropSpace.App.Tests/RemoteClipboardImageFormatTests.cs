using System.Runtime.InteropServices;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DropSpace.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RemoteClipboardImageFormatTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    [DataRow("JPEG", "image/jpeg")]
    [DataRow("BMP", "image/bmp")]
    [DataRow("PNG", "image/png")]
    [DataRow("JPEG", "image/png")]
    public async Task ImportedImageUsesValidatedDecoderFormat(string format, string declaredMime)
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-remote-image-tests", Guid.NewGuid().ToString("N")));
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var queue = controller.DispatcherQueue;
        var payloads = new FilePayloadStore(paths);
        var repository = new SqliteItemRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
        var notifications = new ClipboardNotificationService(NullLogger<ClipboardNotificationService>.Instance);
        var capture = new ClipboardCaptureService(repository, new JsonSettingsService(paths), payloads,
            new FilePreviewCache(paths), new LocalFileReferenceService(), notifications, queue,
            IdentityAppStringLocalizer.Instance, NullLogger<ClipboardCaptureService>.Instance);
        var oleInitialized = false;
        try
        {
            await queue.EnqueueAsync(() =>
            {
                Marshal.ThrowExceptionForHR(OleInitialize(0)); oleInitialized = true;
                return Task.CompletedTask;
            });
            await repository.InitializeAsync();
            var encoder = format switch { "JPEG" => BitmapEncoder.JpegEncoderId, "BMP" => BitmapEncoder.BmpEncoderId, _ => BitmapEncoder.PngEncoderId };
            var bytes = await EncodeAsync(encoder);
            var envelope = ClipboardEnvelopePolicy.CreateImage(Guid.NewGuid(), 1, bytes, declaredMime);

            var item = await capture.ImportRemoteAsync(envelope);

            Assert.IsNotNull(item.Payload);
            Assert.IsNotNull(item.Image);
            var path = payloads.ResolvePath(item.Payload.RelativePath);
            var expectedMime = format switch { "JPEG" => "image/jpeg", "BMP" => "image/bmp", _ => "image/png" };
            Assert.AreEqual(expectedMime, item.Image.MimeType);
            Assert.IsTrue(WindowsImageCodecPreflight.CanDecode(path, Path.GetExtension(path), item.Image.MimeType),
                "Imported bytes, file extension and MIME must pass the same preflight used by image actions.");
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            if (format != "PNG") Assert.AreNotEqual(".png", Path.GetExtension(path));
        }
        finally
        {
            await capture.DisposeAsync(); notifications.Dispose();
            try
            {
                await queue.EnqueueAsync(() =>
                {
                    try { Clipboard.Clear(); }
                    finally { if (oleInitialized) OleUninitialize(); }
                    return Task.CompletedTask;
                });
            }
            finally { await controller.ShutdownQueueAsync(); }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static async Task<byte[]> EncodeAsync(Guid encoderId)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, 1, 1, 96, 96, [32, 64, 128, 255]);
        await encoder.FlushAsync(); stream.Seek(0);
        using var reader = new DataReader(stream);
        var count = checked((uint)stream.Size);
        Assert.AreEqual(count, await reader.LoadAsync(count));
        var result = new byte[count]; reader.ReadBytes(result); return result;
    }

    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}
