using DropSpace.Core.Preview;

namespace DropSpace.Core.Actions;

public static class ItemSelectionResolver
{
    public static ItemSelectionSnapshot ForClickedItem(
        DropItemSnapshot clickedItem,
        IEnumerable<DropItemSnapshot>? selectedItems)
    {
        ArgumentNullException.ThrowIfNull(clickedItem);

        var selected = selectedItems?
            .Where(item => item is not null)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToArray() ?? [];
        if (selected.Length == 0 || selected.All(item => item.Id != clickedItem.Id))
        {
            return new ItemSelectionSnapshot([clickedItem]);
        }

        return new ItemSelectionSnapshot(selected);
    }
}
