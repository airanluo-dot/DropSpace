using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Network;

/// <summary>Counts all roots and discovered entries across one source selection.</summary>
internal sealed class TransferTraversalBudget
{
    // Local preparation policy: allow bounded traversal overhead in addition to every
    // configured file slot. This does not change the manifest or receiver's limits.
    private const int AdditionalTraversalEntries = 4096;
    private readonly long _maximumEntries;
    private long _visitedEntries;

    internal TransferTraversalBudget(long maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        _maximumEntries = maximumEntries;
    }

    internal static TransferTraversalBudget CreateForItemLimit(int maximumItems)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        return new TransferTraversalBudget((long)maximumItems + AdditionalTraversalEntries);
    }

    internal void VisitEntry()
    {
        if (_visitedEntries >= _maximumEntries)
        {
            throw new TransferPreparationLimitException(
                TransferPreparationLimitReason.TraversalEntries, _maximumEntries);
        }

        _visitedEntries++;
    }
}
