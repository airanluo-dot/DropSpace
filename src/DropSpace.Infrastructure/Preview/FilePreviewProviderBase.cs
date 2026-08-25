using System.Text;
using DropSpace.Core.Preview;

namespace DropSpace.Infrastructure.Preview;

public abstract class FilePreviewProviderBase(PreviewLimits limits)
{
    protected PreviewLimits Limits { get; } = limits.Validate();

    protected static string Extension(DropItemSnapshot item) =>
        (item.Extension ?? Path.GetExtension(item.OriginalPath ?? string.Empty)).Trim().ToLowerInvariant();

    protected static FileStream OpenFile(DropItemSnapshot item)
    {
        if (string.IsNullOrWhiteSpace(item.OriginalPath))
        {
            throw new InvalidDataException("The item has no source path.");
        }

        return new FileStream(
            item.OriginalPath,
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
        if (bytes.AsSpan().StartsWith([0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith([0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith([0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);
    }

    protected static IReadOnlyDictionary<string, string> Metadata(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
