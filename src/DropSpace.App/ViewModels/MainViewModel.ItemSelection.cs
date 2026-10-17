using DropSpace.Core.Actions;
using DropSpace.Core.Preview;

namespace DropSpace.App.ViewModels;

public sealed partial class MainViewModel
{
    internal event EventHandler? BatchVisibilityChanged;

    public void ToggleBatchExpanded(ItemCardViewModel card)
    {
        if (card.DropBatchId is not { } batchId)
        {
            return;
        }
        var members = Items.Where(item => item.DropBatchId == batchId).ToArray();
        var expanded = !card.IsBatchExpanded;
        _batchExpansion[batchId] = expanded;
        foreach (var member in members)
        {
            member.IsBatchExpanded = expanded;
            member.IsBatchMemberVisible = member.IsBatchHeader || expanded;
        }
        BatchVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyBatchProjectionState()
    {
        foreach (var group in Items.Where(item => item.IsGrouped && item.DropBatchId is not null)
                     .GroupBy(item => item.DropBatchId))
        {
            var ordered = group.OrderBy(item => item.BatchMetadata?.ItemIndex ?? int.MaxValue).ToArray();
            // Filters, removal, and paging can omit ItemIndex=0. Keep one loaded member
            // actionable until the persisted header is available in this projection.
            // Reevaluate the complete loaded group so later pages never add a second header.
            var header = ordered.FirstOrDefault(item => item.BatchMetadata?.ItemIndex == 0) ?? ordered[0];
            var expanded = _batchExpansion.TryGetValue(group.Key!.Value, out var value) && value;
            for (var index = 0; index < ordered.Length; index++)
            {
                var card = ordered[index];
                var isHeader = ReferenceEquals(card, header);

                card.IsBatchHeader = isHeader;
                card.IsBatchExpanded = expanded;
                card.IsBatchMemberVisible = isHeader || expanded;
            }
        }
        BatchVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public ItemSelectionSnapshot ResolveActionSelection(
        ItemCardViewModel card,
        IEnumerable<ItemCardViewModel>? selectedCards = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        var clicked = DropItemSnapshot.FromItem(card.Item);
        var selected = selectedCards?
            .Where(IsCurrentVisibleItem)
            .Select(candidate => DropItemSnapshot.FromItem(candidate.Item));
        return ItemSelectionResolver.ForClickedItem(clicked, selected);
    }

    internal bool IsCurrentVisibleItem(ItemCardViewModel card) =>
        IsCollectionVisible && card.IsBatchMemberVisible && Items.Contains(card);
}
