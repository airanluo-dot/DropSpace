using System.Runtime.Versioning;

namespace DropSpace.App.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsImageCodecPreflightTests
{
    [TestMethod]
    public void DetectsKnownImageSignaturesAndRejectsUnknownData()
    {
        Assert.AreEqual(
            ".png",
            WindowsImageCodecPreflight.DetectExtension(
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            ]));
        Assert.AreEqual(".jpg", WindowsImageCodecPreflight.DetectExtension([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.AreEqual(".gif", WindowsImageCodecPreflight.DetectExtension("GIF89a"u8));
        Assert.AreEqual(".bmp", WindowsImageCodecPreflight.DetectExtension("BM"u8));
        Assert.AreEqual(".tiff", WindowsImageCodecPreflight.DetectExtension([0x49, 0x49, 0x2A, 0x00]));
        Assert.AreEqual(".webp", WindowsImageCodecPreflight.DetectExtension("RIFF0000WEBP"u8));
        Assert.AreEqual(".ico", WindowsImageCodecPreflight.DetectExtension([0x00, 0x00, 0x01, 0x00]));
        Assert.IsNull(WindowsImageCodecPreflight.DetectExtension("not-an-image"u8));
    }

    [TestMethod]
    public void DeclaredExtensionMustAgreeWithActualSignature()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "image.jpg");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(
                path,
                [
                    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                ]);

            Assert.IsFalse(WindowsImageCodecPreflight.CanDecode(path, ".jpg", "image/jpeg"));
            Assert.IsFalse(WindowsImageCodecPreflight.CanDecode(path, ".png", "image/png"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void InstalledPngEncoderIsRequiredForPngCapability()
    {
        Assert.IsTrue(WindowsImageCodecPreflight.CanEncode(".png"));
        Assert.IsTrue(WindowsImageCodecPreflight.CanEncode("image/png"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
