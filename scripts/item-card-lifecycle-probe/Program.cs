using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

var observed = CreateItem("original.txt");
var card = new ItemCardViewModel(observed, IdentityAppStringLocalizer.Instance);
var staleThumbnail = new BitmapImage();
var staleStorage = new ProbeStorageItem();
card.Thumbnail = staleThumbnail;
card.DragStorageItem = staleStorage;
var delayedRelease = Signal();
var stalePublication = PublishAfterAsync(card, delayedRelease.Task, staleThumbnail, staleStorage);
var replacement = observed with
{
    Revision = observed.Revision + 1,
    Title = "replacement.txt",
    File = observed.File! with { OriginalPath = "replacement.txt", NormalizedPath = "replacement.txt" },
};
Check(card.TryUpdate(replacement), "replacement was rejected");
Check(card.Thumbnail is null && card.DragStorageItem is null, "relink did not immediately clear obsolete assets");
delayedRelease.SetResult();
Check(!await stalePublication, "late assets for the previous reference were published");
Check(card.Item == replacement && card.Thumbnail is null && card.DragStorageItem is null, "late publication modified the replacement");

var currentThumbnail = new BitmapImage();
var currentStorage = new ProbeStorageItem();
var pinRelease = Signal();
var pinPublication = PublishAfterAsync(card, pinRelease.Task, currentThumbnail, currentStorage);
var pinned = replacement with { Revision = replacement.Revision + 1, IsPinned = true };
Check(card.TryUpdate(pinned), "pin update was rejected");
pinRelease.SetResult();
Check(await pinPublication, "pin change invalidated still-current pending content");
Check(ReferenceEquals(card.Thumbnail, currentThumbnail) && ReferenceEquals(card.DragStorageItem, currentStorage), "current assets were not assigned");
Check(!card.TryUpdate(replacement) && card.Item.IsPinned, "old record undid a newer pin change");

var used = pinned with { Revision = pinned.Revision + 1, LastUsedAtUtc = DateTimeOffset.UtcNow };
var refreshed = new ItemCardViewModel(used, IdentityAppStringLocalizer.Instance);
Check(card.TryMergePresentation(refreshed), "same-content overlay refresh failed");
Check(ReferenceEquals(card.Thumbnail, currentThumbnail) && ReferenceEquals(card.DragStorageItem, currentStorage), "an unloaded equivalent card cleared valid assets");
var obsolete = new ItemCardViewModel(observed, IdentityAppStringLocalizer.Instance)
{ Thumbnail = staleThumbnail, DragStorageItem = staleStorage };
Check(!card.TryMergePresentation(obsolete), "stale overlay snapshot was merged");
Check(card.Item == used && ReferenceEquals(card.Thumbnail, currentThumbnail), "stale overlay merge changed current content");

var deleteRelease = Signal();
var deletePublication = PublishAfterAsync(card, deleteRelease.Task, staleThumbnail, staleStorage);
var deletedGeneration = card.PresentationGeneration;
card.RetirePresentation();
deleteRelease.SetResult();
Check(!await deletePublication, "retired card accepted a late preparation");
Check(!card.TryUpdate(used), "retired card accepted its old model state");
Check(card.TryUpdate(used with { Revision = used.Revision + 1 }), "new authoritative revision did not restore a retired card");
Check(!card.IsPresentationCurrent(deletedGeneration), "restoring a card revived an old preparation generation");
Console.WriteLine("PASS one card lifecycle scenario: delayed relink/overlay/delete results isolated; pin/last-used changes preserve current assets; a new revision can restore a retired card.");

static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task<bool> PublishAfterAsync(ItemCardViewModel card, Task ready, BitmapImage thumbnail, IStorageItem storage)
{
    var generation = card.PresentationGeneration;
    await ready;
    return card.TryApplyPresentation(generation, () =>
    {
        card.Thumbnail = thumbnail;
        card.DragStorageItem = storage;
    });
}
static DropItem CreateItem(string path) => new(Guid.NewGuid(), ItemSource.Space, ItemKind.File,
    path, DateTimeOffset.UtcNow, null, false, ItemStatus.Available, path, 1, null, null,
    new(path, path, FileEntryKind.File, ".txt", 7, DateTimeOffset.UtcNow, null, null),
    null, null, null, null);
