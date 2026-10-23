using DropSpace.Core.Models;

namespace DropSpace.App.ViewModels;

public sealed partial class MainViewModel
{
    // The island queries the same repository and prepares the same card assets as
    // the main window, without changing its navigation or search query.
    internal async Task<IReadOnlyList<ItemCardViewModel>> QueryRetrievalItemsAsync(
        string query, ItemSource? source, bool pinnedOnly, CancellationToken cancellationToken)
    {
        var items = await Task.Run(() => _repository.QueryAsync(
            new ItemQuery(Source: source, PinnedOnly: pinnedOnly, Search: query, Limit: 100),
            cancellationToken), cancellationToken);
        var cards = items.Select(item => new ItemCardViewModel(item, _strings)).ToArray();
        await Task.WhenAll(cards.Where(card => card.Item.Status != ItemStatus.Processing)
            .Select(card => LoadThumbnailSafelyAsync(card, cancellationToken)));
        return cards.Where(card => !card.IsPresentationRetired).ToArray();
    }

    internal Task<DropItem?> ValidateRetrievalItemAsync(ItemCardViewModel card, CancellationToken cancellationToken) =>
        RefreshFileAvailabilityAsync(card, cancellationToken);

    internal async Task<ItemCardViewModel?> ReadRetrievalItemAsync(Guid id, CancellationToken token)
    {
        var item = await Task.Run(() => _repository.GetAsync(id, token), token);
        if (item is null) return null;
        var card = new ItemCardViewModel(item, _strings);
        if (item.Status != ItemStatus.Processing) await LoadThumbnailSafelyAsync(card, token);
        return card.IsPresentationRetired ? null : card;
    }

    internal async Task RefreshRetainedRetrievalAsync()
    {
        await PublishSpaceProjectionChangedAsync(CancellationToken.None);
        await ReloadAsync();
    }
}
