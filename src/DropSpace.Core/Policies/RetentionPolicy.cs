using DropSpace.Core.Models;

namespace DropSpace.Core.Policies;

public static class RetentionPolicy
{
    public static IReadOnlyList<Guid> SelectExpired(
        IEnumerable<DropItem> items,
        DateTimeOffset ageCutoffUtc,
        int countLimit)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countLimit);

        var candidates = items
            .Where(item => item.Source == ItemSource.Clipboard && !item.IsPinned)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToArray();

        return candidates
            .Where((item, index) => item.CreatedAtUtc < ageCutoffUtc || index >= countLimit)
            .Select(item => item.Id)
            .Distinct()
            .ToArray();
    }
}
