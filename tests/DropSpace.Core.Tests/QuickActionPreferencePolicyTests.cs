using DropSpace.Core.Actions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class QuickActionPreferencePolicyTests
{
    [TestMethod]
    public void AutomaticUsesRegistryOrderAndKeepsRemainingActionsInMore()
    {
        var selection = Selection(ItemKind.File);
        var available = Capabilities(ItemActionId.HashSha256, ItemActionId.CompressZip, ItemActionId.GenerateQr, ItemActionId.CopyPath);

        var result = QuickActionPreferencePolicy.Partition(available, selection, null);

        CollectionAssert.AreEqual(
            new[] { ItemActionId.HashSha256, ItemActionId.CompressZip, ItemActionId.GenerateQr },
            result.Primary.Select(capability => capability.Descriptor.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { ItemActionId.CopyPath },
            result.More.Select(capability => capability.Descriptor.Id).ToArray());
    }

    [TestMethod]
    public void CustomOrderSkipsUnavailableActionsWithoutRandomReplacement()
    {
        var selection = Selection(ItemKind.Image);
        var available = Capabilities(ItemActionId.HashSha256, ItemActionId.GenerateQr, ItemActionId.ResizeImage);
        var preferences = QuickActionPreferencePolicy.CreateAutomaticPreferences();
        preferences[QuickActionProfile.Image] = new(
            false,
            ItemActionId.ConvertImage,
            ItemActionId.ResizeImage,
            null);

        var result = QuickActionPreferencePolicy.Partition(available, selection, preferences);

        CollectionAssert.AreEqual(
            new[] { ItemActionId.ResizeImage },
            result.Primary.Select(capability => capability.Descriptor.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { ItemActionId.HashSha256, ItemActionId.GenerateQr },
            result.More.Select(capability => capability.Descriptor.Id).ToArray());
    }

    [TestMethod]
    public void MixedSelectionFallsBackToAutomaticOrdering()
    {
        var selection = new ItemSelectionSnapshot([Snapshot(ItemKind.File), Snapshot(ItemKind.Image)]);
        var available = Capabilities(ItemActionId.HashSha256, ItemActionId.ResizeImage, ItemActionId.GenerateQr, ItemActionId.CopyPath);
        var preferences = QuickActionPreferencePolicy.CreateAutomaticPreferences();
        preferences[QuickActionProfile.File] = new(false, ItemActionId.CopyPath, null, null);

        var result = QuickActionPreferencePolicy.Partition(available, selection, preferences);

        CollectionAssert.AreEqual(
            new[] { ItemActionId.HashSha256, ItemActionId.ResizeImage, ItemActionId.GenerateQr },
            result.Primary.Select(capability => capability.Descriptor.Id).ToArray());
    }

    [TestMethod]
    public void PreferenceValidationRejectsDuplicateSlots()
    {
        var preference = new QuickActionPreference(false, ItemActionId.HashSha256, ItemActionId.HashSha256, null);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => preference.Validate());
    }

    private static ItemSelectionSnapshot Selection(ItemKind kind) => new([Snapshot(kind)]);

    private static DropItemSnapshot Snapshot(ItemKind kind) => new(
        Guid.NewGuid(),
        kind,
        ItemStatus.Available,
        "Test",
        null,
        null,
        null,
        kind == ItemKind.Image ? "image/png" : null,
        kind == ItemKind.Text ? "text" : null,
        kind == ItemKind.Url ? new UrlMetadata("https://example.test", "example.test", "example.test", "https") : null,
        1);

    private static ItemActionCapability[] Capabilities(params ItemActionId[] ids) => ids
        .Select((id, index) => new ItemActionCapability(
            true,
            null,
            new ItemActionDescriptor(id, id.ToString(), "", ItemActionGroup.General, index, false, false)))
        .ToArray();
}
