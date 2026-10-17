using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class BatchSelectionSafetyTests
{
    [TestMethod]
    public void CollapsingBatchExcludesPreviouslySelectedMembersFromQuickActions()
    {
        var batch = Guid.NewGuid();
        var header = Card(batch, 0);
        var member = Card(batch, 1);
        var view = CreateView(header, member);
        ApplyProjection(view);
        var visibilityNotifications = new List<bool>();
        view.BatchVisibilityChanged += (_, _) => visibilityNotifications.Add(member.IsBatchMemberVisible);
        view.ToggleBatchExpanded(header);
        var selected = new[] { header, member };
        Assert.AreEqual(2, view.ResolveActionSelection(header, selected).Items.Count);

        view.ToggleBatchExpanded(header);

        Assert.IsFalse(view.IsCurrentVisibleItem(member));
        CollectionAssert.AreEqual(new[] { header.Id },
            view.ResolveActionSelection(header, selected).Items.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { true, false }, visibilityNotifications);
    }

    [TestMethod]
    public void LaterPageHeaderReplacementRetiresHiddenSelection()
    {
        var batch = Guid.NewGuid();
        var partialHeader = Card(batch, 1);
        var otherBatchHeader = Card(Guid.NewGuid(), 0);
        var view = CreateView(partialHeader, otherBatchHeader);
        ApplyProjection(view);
        var selected = new[] { partialHeader, otherBatchHeader };
        var originalHeader = Card(batch, 0);
        var notifiedAfterHeaderReplacement = false;
        view.BatchVisibilityChanged += (_, _) =>
            notifiedAfterHeaderReplacement = !partialHeader.IsBatchMemberVisible && originalHeader.IsBatchMemberVisible;

        view.Items.Add(originalHeader);
        ApplyProjection(view);

        Assert.IsFalse(view.IsCurrentVisibleItem(partialHeader));
        Assert.IsTrue(view.IsCurrentVisibleItem(originalHeader));
        Assert.IsTrue(notifiedAfterHeaderReplacement);
        CollectionAssert.AreEqual(new[] { otherBatchHeader.Id },
            view.ResolveActionSelection(otherBatchHeader, selected).Items.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void RemovedCardsCannotRemainInQuickActionSelection()
    {
        var visible = Card(Guid.NewGuid(), 0);
        var removed = Card(Guid.NewGuid(), 0);
        var view = CreateView(visible, removed);
        ApplyProjection(view);
        var selected = new[] { visible, removed };

        view.Items.Remove(removed);

        Assert.IsFalse(view.IsCurrentVisibleItem(removed));
        CollectionAssert.AreEqual(new[] { visible.Id },
            view.ResolveActionSelection(visible, selected).Items.Select(item => item.Id).ToArray());
    }

    private static MainViewModel CreateView(params ItemCardViewModel[] cards)
    {
        var view = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        SetField(view, "<Items>k__BackingField", new ObservableCollection<ItemCardViewModel>(cards));
        SetField(view, "_batchExpansion", new Dictionary<Guid, bool>());
        return view;
    }

    private static void ApplyProjection(MainViewModel view) => typeof(MainViewModel)
        .GetMethod("ApplyBatchProjectionState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);

    private static void SetField(MainViewModel view, string name, object value) => typeof(MainViewModel)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);

    private static ItemCardViewModel Card(Guid batch, int index) => new(
        new DropItem(Guid.NewGuid(), ItemSource.Space, ItemKind.File, "file " + index,
            DateTimeOffset.UtcNow, null, false, ItemStatus.Available, string.Empty, 1, null,
            JsonSerializer.Serialize(new DropBatchMetadata(batch, null, index, 3, "manual")),
            null, null, null, null, null), IdentityAppStringLocalizer.Instance);
}
