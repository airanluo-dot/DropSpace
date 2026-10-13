namespace DropSpace.Core.Transfer;

public enum TransferPreparationLimitReason
{
    TraversalEntries,
    RelativePathLength,
}

/// <summary>A source selection exceeded a local preparation limit before a transfer was offered.</summary>
public sealed class TransferPreparationLimitException : IOException
{
    public TransferPreparationLimitException(TransferPreparationLimitReason reason, long limit)
        : base(CreateMessage(reason, limit))
    {
        Reason = reason;
        Limit = limit;
    }

    public TransferPreparationLimitReason Reason { get; }

    public long Limit { get; }

    private static string CreateMessage(TransferPreparationLimitReason reason, long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return reason switch
        {
            TransferPreparationLimitReason.TraversalEntries => FormattableString.Invariant(
                $"The transfer source traversal exceeded its limit of {limit} entries."),
            TransferPreparationLimitReason.RelativePathLength => FormattableString.Invariant(
                $"A transfer source relative path exceeded its limit of {limit} characters."),
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
    }
}
