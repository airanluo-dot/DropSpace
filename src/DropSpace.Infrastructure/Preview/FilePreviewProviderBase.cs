using System.Text;
using DropSpace.Core.Content;
using DropSpace.Core.Preview;

namespace DropSpace.Infrastructure.Preview;

public abstract class FilePreviewProviderBase(PreviewLimits limits, IItemContentResolver contentResolver)
{
    protected PreviewLimits Limits { get; } = limits.Validate();

    protected IItemContentResolver ContentResolver { get; } = contentResolver;

    protected string Extension(DropItemSnapshot item) => ContentResolver.Resolve(item).Extension ?? string.Empty;

    protected FileStream OpenFile(DropItemSnapshot item)
    {
        var content = ContentResolver.Resolve(item);
        if (!content.HasReadablePath)
        {
            throw new InvalidDataException(content.UnavailableReason ?? "The item has no readable source path.");
        }

        return new FileStream(
            content.ReadablePath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    protected static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        var total = 0L;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("The preview source exceeds its bounded read limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    protected static string DecodeText(byte[] bytes)
    {
        // UTF-32 little endian shares UTF-16's leading BOM bytes. Recognize the
        // full four-byte BOM first so supplementary characters stay intact.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(bytes, 4, bytes.Length - 4);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);
    }

    protected static IReadOnlyDictionary<string, string> Metadata(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
