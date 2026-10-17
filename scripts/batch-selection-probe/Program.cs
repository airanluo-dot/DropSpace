using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

CollapseSelection();
ReplacePartialHeader();
RetireSelection();

static void CollapseSelection()
{
    var batch = Guid.NewGuid();
    var header = Card(batch, 0);
    var member = Card(batch, 1);
    var view = View(header, member);
    var visibilityNotifications = new List<bool>();
    view.BatchVisibilityChanged += (_, _) => visibilityNotifications.Add(member.IsBatchMemberVisible);
    view.ToggleBatchExpanded(header);
    var selected = new[] { header, member };
    Check(view.ResolveActionSelection(header, selected).Items.Count == 2, "expanded selection lost a member");

    view.ToggleBatchExpanded(header);

    Check(!view.IsCurrentVisibleItem(member), "collapsed member remained actionable");
    CheckSelection(view, header, selected, header);
    view.ToggleBatchExpanded(header);
    CheckSelection(view, header, selected, header, member);
    Check(visibilityNotifications.SequenceEqual(new[] { true, false, true }),
        "selection cleanup was not notified after each completed visibility change");
    Console.WriteLine("PASS collapse: hidden selected members are excluded; expanding restores eligibility.");
}

static void ReplacePartialHeader()
{
    var batch = Guid.NewGuid();
    var partialHeader = Card(batch, 1);
    var otherHeader = Card(Guid.NewGuid(), 0);
    var view = View(partialHeader, otherHeader);
    var selected = new[] { partialHeader, otherHeader };
    var originalHeader = Card(batch, 0);
    var notifiedAfterHeaderReplacement = false;
    view.BatchVisibilityChanged += (_, _) =>
        notifiedAfterHeaderReplacement = !partialHeader.IsBatchMemberVisible && originalHeader.IsBatchMemberVisible;

    view.Items.Add(originalHeader);
    view.ApplyProjectionForProbe();

    Check(!view.IsCurrentVisibleItem(partialHeader), "previous partial header remained actionable");
    Check(view.IsCurrentVisibleItem(originalHeader), "new batch header was not actionable");
    Check(notifiedAfterHeaderReplacement, "header replacement did not notify selection cleanup");
    CheckSelection(view, otherHeader, selected, otherHeader);
    CheckSelection(view, originalHeader, selected, originalHeader);
    Console.WriteLine("PASS paging: an original header retires the previous hidden representative without losing other selection.");
}

static void RetireSelection()
{
    var visible = Card(Guid.NewGuid(), 0);
    var removed = Card(Guid.NewGuid(), 0);
    var view = View(visible, removed);
    var selected = new[] { visible, removed };

    view.Items.Remove(removed);

    Check(!view.IsCurrentVisibleItem(removed), "removed card remained actionable");
    CheckSelection(view, visible, selected, visible);
    view.IsCollectionVisible = false;
    Check(!view.IsCurrentVisibleItem(visible), "hidden collection left an actionable card");
    Console.WriteLine("PASS retirement: removed cards leave action snapshots; hidden collections reject item actions.");
}

static MainViewModel View(params ItemCardViewModel[] cards)
{
    var view = new MainViewModel();
    foreach (var card in cards) view.Items.Add(card);
    view.ApplyProjectionForProbe();
    return view;
}

static void CheckSelection(MainViewModel view, ItemCardViewModel clicked,
    ItemCardViewModel[] selected, params ItemCardViewModel[] expected) =>
    Check(view.ResolveActionSelection(clicked, selected).Items.Select(item => item.Id)
        .SequenceEqual(expected.Select(item => item.Id)), "unexpected action selection");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static ItemCardViewModel Card(Guid batch, int index) => new(
    new DropItem(Guid.NewGuid(), ItemSource.Space, ItemKind.File, "file " + index,
        DateTimeOffset.UtcNow, null, false, ItemStatus.Available, string.Empty, 1, null,
        JsonSerializer.Serialize(new DropBatchMetadata(batch, null, index, 3, "manual")),
        null, null, null, null, null), IdentityAppStringLocalizer.Instance);
