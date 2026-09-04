using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace DropSpace.Core.DragDrop;

/// <summary>
/// A small signal lane for the Smart Drag observer. Reliable lanes are unbounded so a burst of
/// lifecycle signals cannot be silently discarded. Lossy lanes keep only the newest signal and
/// expose replacement diagnostics for high-frequency pointer movement.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Queue is the established name for this Smart Drag signal contract.")]
public sealed class DragSignalQueue<T>
{
    private readonly Channel<T> _channel;
    private readonly object _writeGate = new();
    private readonly bool _lossy;
    private long _replacedWrites;
    private long _writeFailures;

    public DragSignalQueue(bool reliable, int lossyCapacity = 1)
    {
        if (!reliable && lossyCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lossyCapacity));
        }

        _lossy = !reliable;
        _channel = reliable
            ? Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            })
            : Channel.CreateBounded<T>(new BoundedChannelOptions(lossyCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    public long ReplacedWriteCount => Interlocked.Read(ref _replacedWrites);

    public long WriteFailureCount => Interlocked.Read(ref _writeFailures);

    public bool TryWrite(T value)
    {
        lock (_writeGate)
        {
            if (_lossy && _channel.Reader.TryPeek(out _))
            {
                Interlocked.Increment(ref _replacedWrites);
            }

            if (_channel.Writer.TryWrite(value))
            {
                return true;
            }

            Interlocked.Increment(ref _writeFailures);
            return false;
        }
    }

    public bool TryPeek([MaybeNullWhen(false)] out T value) => _channel.Reader.TryPeek(out value);

    public bool TryRead([MaybeNullWhen(false)] out T value) => _channel.Reader.TryRead(out value);

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();
}
