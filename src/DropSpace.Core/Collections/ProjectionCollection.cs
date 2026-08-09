namespace DropSpace.Core.Collections;

/// <summary>
/// Applies an identity-based mutation to a UI projection. Different views may wrap the same domain
/// record in different card instances; object-reference removal is therefore never authoritative.
/// </summary>
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
}
