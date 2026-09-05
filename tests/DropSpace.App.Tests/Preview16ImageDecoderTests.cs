using DropSpace.App.Services;
using Windows.Storage.Streams;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DropSpace.App.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.10240")]
public sealed class Preview16ImageDecoderTests
{
    [TestMethod]
    [DataRow("PNG", "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAGUlEQVR4nGM0SpnGQApgIkn1qIZRDUNKAwDIQQFM/x7kSwAAAABJRU5ErkJggg==")]
    [DataRow("JPEG", "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAQABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDmqKKK908U/9k=")]
    [DataRow("GIF", "R0lGODdhEAAQAIEAADJklgAAAAAAAAAAACwAAAAAEAAQAEAIHQABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFgQEBADs=")]
    [DataRow("BMP", "Qk02AwAAAAAAADYAAAAoAAAAEAAAABAAAAABABgAAAAAAAADAADEDgAAxA4AAAAAAAAAAAAAlmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQylmQy")]
    [DataRow("TIFF", "SUkqAAgAAAAKAAABBAABAAAAEAAAAAEBBAABAAAAEAAAAAIBAwADAAAAhgAAAAMBAwABAAAAAQAAAAYBAwABAAAAAgAAABEBBAABAAAAjAAAABUBAwABAAAAAwAAABYBBAABAAAAEAAAABcBBAABAAAAAAMAABwBAwABAAAAAQAAAAAAAAAIAAgACAAyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJYyZJY=")]
    [DataRow("WEBP", "UklGRjgAAABXRUJQVlA4ICwAAADwAQCdASoQABAAAUAmJaACdLoB+AAETAAA/vAb3/8btxhvwXf770BvAwAAAA==")]
    [DataRow("WEBP_LOSSLESS", "UklGRhoAAABXRUJQVlA4TA4AAAAvD8ADAAcQEf0PRET/Aw==")]
    [DataRow("WEBP_EXTENDED", "UklGRkgAAABXRUJQVlA4WAoAAAAQAAAADwAADwAAQUxQSAoAAAABB1CyiAhERP8DVlA4IBgAAAAwAQCdASoQABAAAUAmJaQAA3AA/v0gUAA=")]
    public async Task PlatformMetadataAlwaysPrecedesDecode(string format, string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        try
        {
            var decoder = await ImageDecoderPreflight.ValidateAsync(stream, 100000, 256);
            Assert.AreEqual(16u, decoder.PixelWidth);
            Assert.AreEqual(16u, decoder.PixelHeight);
        }
        catch (COMException) when (format.StartsWith("WEBP", StringComparison.Ordinal))
        {
            // Windows installations without the optional WebP codec must reject it.
            return;
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ImageDecoderPreflight.ValidateAsync(stream, 100000, 255));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ImageDecoderPreflight.ValidateAsync(stream, 1, 256));
    }
}
