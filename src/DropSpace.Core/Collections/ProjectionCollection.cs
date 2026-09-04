namespace DropSpace.Core.Collections;

/// <summary>
/// Applies an identity-based mutation to a UI projection. Different views may wrap the same domain
/// record in different card instances; object-reference removal is therefore never authoritative.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Collection is the established name for this identity projection contract.")]
public static class ProjectionCollection
{
    public static bool RemoveById<T, TId>(ICollection<T> collection, Func<T, TId> idSelector, TId id)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(idSelector);
        foreach (var candidate in collection)
        {
            if (EqualityComparer<TId>.Default.Equals(idSelector(candidate), id))
            {
                return collection.Remove(candidate);
            }
        }

        return false;
    }

    public static void SynchronizeById<T, TId>(
        IList<T> target,
        IReadOnlyList<T> source,
        Func<T, TId> idSelector,
        Action<T, T>? updateExisting = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(idSelector);
        var desiredIds = source.Select(idSelector).ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(idSelector(target[index])))
            {
                target.RemoveAt(index);
            }
        }

        for (var desiredIndex = 0; desiredIndex < source.Count; desiredIndex++)
        {
            var incoming = source[desiredIndex];
            var incomingId = idSelector(incoming);
            if (desiredIndex < target.Count &&
                EqualityComparer<TId>.Default.Equals(idSelector(target[desiredIndex]), incomingId))
            {
                updateExisting?.Invoke(target[desiredIndex], incoming);
                continue;
            }

            var existingIndex = -1;
            for (var index = desiredIndex + 1; index < target.Count; index++)
            {
                if (EqualityComparer<TId>.Default.Equals(idSelector(target[index]), incomingId))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existing = target[existingIndex];
                target.RemoveAt(existingIndex);
                target.Insert(desiredIndex, existing);
                updateExisting?.Invoke(existing, incoming);
            }
            else
            {
                target.Insert(desiredIndex, incoming);
            }
        }
    }
}
