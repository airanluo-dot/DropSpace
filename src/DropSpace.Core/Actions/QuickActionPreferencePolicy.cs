using DropSpace.Core.Models;

namespace DropSpace.Core.Actions;

public enum QuickActionProfile
{
    File = 1,
    Image = 2,
    Text = 3,
    Url = 4,
}

public sealed record QuickActionPreference(
    bool IsAutomatic,
    ItemActionId? First,
    ItemActionId? Second,
    ItemActionId? Third)
{
    public static QuickActionPreference Automatic { get; } = new(true, null, null, null);

    public IReadOnlyList<ItemActionId?> Slots => [First, Second, Third];

    public void Validate()
    {
        var selected = Slots
            .Where(action => action is not null)
            .Select(action => action!.Value)
            .ToArray();

        if (selected.Any(action => !Enum.IsDefined(action)) || selected.Distinct().Count() != selected.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(QuickActionPreference), "Quick Action slots must contain unique defined action IDs.");
        }
    }
}

public sealed record QuickActionPartition(
    IReadOnlyList<ItemActionCapability> Primary,
    IReadOnlyList<ItemActionCapability> More);

public sealed class QuickActionPreferenceCollection :
    Dictionary<QuickActionProfile, QuickActionPreference>,
    IEquatable<QuickActionPreferenceCollection>
{
    public QuickActionPreferenceCollection()
    {
    }

    public QuickActionPreferenceCollection(IEnumerable<KeyValuePair<QuickActionProfile, QuickActionPreference>> values)
        : base(values.ToDictionary(entry => entry.Key, entry => entry.Value))
    {
    }

    public bool Equals(QuickActionPreferenceCollection? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null && Count == other.Count &&
            this.All(entry => other.TryGetValue(entry.Key, out var value) &&
                              EqualityComparer<QuickActionPreference>.Default.Equals(entry.Value, value));
    }

    public override bool Equals(object? obj) => Equals(obj as QuickActionPreferenceCollection);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in this.OrderBy(entry => entry.Key))
        {
            hash.Add(entry.Key);
            hash.Add(entry.Value);
        }

        return hash.ToHashCode();
    }
}

public static class QuickActionPreferencePolicy
{
    public const int MaximumPrimaryActions = 3;

    public static QuickActionPreferenceCollection CreateAutomaticPreferences() =>
        new(Enum.GetValues<QuickActionProfile>()
            .Select(profile => new KeyValuePair<QuickActionProfile, QuickActionPreference>(profile, QuickActionPreference.Automatic)));

    public static QuickActionProfile? ResolveProfile(ItemSelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.IsEmpty)
        {
            return null;
        }

        var profiles = selection.Items
            .Select(GetProfile)
            .Distinct()
            .ToArray();

        return profiles.Length == 1 ? profiles[0] : null;
    }

    public static QuickActionPartition Partition(
        IReadOnlyList<ItemActionCapability> available,
        ItemSelectionSnapshot selection,
        IReadOnlyDictionary<QuickActionProfile, QuickActionPreference>? preferences)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(selection);

        var distinctAvailable = available
            .Where(capability => capability.IsAvailable)
            .GroupBy(capability => capability.Descriptor.Id)
            .Select(group => group.First())
            .ToArray();

        var profile = ResolveProfile(selection);
        var preference = profile is { } resolvedProfile &&
                         preferences is not null &&
                         preferences.TryGetValue(resolvedProfile, out var configured)
            ? configured
            : QuickActionPreference.Automatic;

        preference ??= QuickActionPreference.Automatic;
        preference.Validate();

        if (preference.IsAutomatic)
        {
            return new QuickActionPartition(
                distinctAvailable.Take(MaximumPrimaryActions).ToArray(),
                distinctAvailable.Skip(MaximumPrimaryActions).ToArray());
        }

        var byId = distinctAvailable.ToDictionary(capability => capability.Descriptor.Id);
        var primary = new List<ItemActionCapability>(MaximumPrimaryActions);
        foreach (var actionId in preference.Slots)
        {
            if (actionId is { } id && byId.TryGetValue(id, out var capability) &&
                primary.All(existing => existing.Descriptor.Id != id))
            {
                primary.Add(capability);
            }
        }

        var selectedIds = primary.Select(capability => capability.Descriptor.Id).ToHashSet();
        var more = distinctAvailable
            .Where(capability => !selectedIds.Contains(capability.Descriptor.Id))
            .ToArray();

        return new QuickActionPartition(primary, more);
    }

    private static QuickActionProfile GetProfile(DropItemSnapshot item) => item.Kind switch
    {
        ItemKind.Image => QuickActionProfile.Image,
        ItemKind.Url => QuickActionProfile.Url,
        ItemKind.Text or ItemKind.Code or ItemKind.Color => QuickActionProfile.Text,
        _ => QuickActionProfile.File,
    };
}
