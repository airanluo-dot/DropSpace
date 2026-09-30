using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class FullAuditBatchProjectionRegressionTests
{
    [TestMethod]
    public void PinnedMembersWithoutOriginalHeaderRetainOneActionableRepresentative()
    {
        var batch = Guid.NewGuid();
        var view = CreateView(Card(batch, 2), Card(batch, 1));
        Apply(view);
        Assert.AreEqual(1, view.Items.Count(item => item.IsBatchHeader));
        Assert.AreEqual(1, view.Items.Count(item => item.IsBatchMemberVisible));
        Assert.IsTrue(view.Items.Single(item => item.BatchMetadata!.ItemIndex == 1).IsBatchHeader);
    }

    [TestMethod]
    public void LaterPageTransfersHeaderOwnershipWithoutCreatingDuplicateHeaders()
    {
        var batch = Guid.NewGuid();
        var view = CreateView(Card(batch, 2), Card(batch, 1));
        Apply(view);
        var originalHeader = Card(batch, 0);
        view.Items.Add(originalHeader);
        Apply(view);
        Assert.AreEqual(1, view.Items.Count(item => item.IsBatchHeader));
        Assert.AreEqual(1, view.Items.Count(item => item.IsBatchMemberVisible));
        Assert.IsTrue(originalHeader.IsBatchHeader);
    }

    [TestMethod]
    public void ProjectionAfterHeaderRemovalElectsRemainingMember()
    {
        var batch = Guid.NewGuid();
        var header = Card(batch, 0);
        var view = CreateView(header, Card(batch, 1));
        Apply(view);
        view.Items.Remove(header);
        Apply(view);
        Assert.IsTrue(view.Items.Single().IsBatchHeader);
        Assert.IsTrue(view.Items.Single().IsBatchMemberVisible);
    }

    [TestMethod]
    public void ExpandedPartialBatchRetainsAllLoadedMembers()
    {
        var batch = Guid.NewGuid();
        var view = CreateView(Card(batch, 1), Card(batch, 2));
        SetField(view, "_batchExpansion", new Dictionary<Guid, bool> { [batch] = true });
        Apply(view);
        Assert.AreEqual(1, view.Items.Count(item => item.IsBatchHeader));
        Assert.IsTrue(view.Items.All(item => item.IsBatchMemberVisible && item.IsBatchExpanded));
    }

    // These projection-only paths require neither a dispatcher nor native WinUI controls.
    private static MainViewModel CreateView(params ItemCardViewModel[] items)
    {
        var view = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        SetField(view, "<Items>k__BackingField", new ObservableCollection<ItemCardViewModel>(items));
        SetField(view, "_batchExpansion", new Dictionary<Guid, bool>());
        return view;
    }

    private static void Apply(MainViewModel view) => typeof(MainViewModel)
        .GetMethod("ApplyBatchProjectionState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);

    private static void SetField(MainViewModel view, string name, object value) => typeof(MainViewModel)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);

    private static ItemCardViewModel Card(Guid batch, int index) => new(
        new DropItem(Guid.NewGuid(), ItemSource.Space, ItemKind.File, "file " + index,
            DateTimeOffset.UtcNow, null, true, ItemStatus.Available, string.Empty, 1, null,
            JsonSerializer.Serialize(new DropBatchMetadata(batch, null, index, 3, "manual")),
            null, null, null, null, null), IdentityAppStringLocalizer.Instance);
}
