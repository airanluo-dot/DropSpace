// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
namespace DropSpace.Infrastructure.Downloads;

/// <summary>Builds disjoint inclusive byte intervals without multiplication overflow.</summary>
public static class HttpByteRangePlanner
{
    public readonly record struct ByteRange(long From, long To)
    {
        public long Length => To - From + 1;
    }

    public static IReadOnlyList<ByteRange> Create(long length, int connections, long minimumPartSize = 2 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumPartSize);
        var count = (int)Math.Min(Math.Clamp(connections, 1, DownloadConnectionBudget.Maximum), Math.Max(1, length / minimumPartSize));
        var quotient = length / count;
        var remainder = length % count;
        var ranges = new ByteRange[count];
        long offset = 0;
        for (var index = 0; index < count; index++)
        {
            var size = quotient + (index < remainder ? 1 : 0);
            ranges[index] = new ByteRange(offset, offset + size - 1);
            offset += size;
        }
        return ranges;
    }
}
