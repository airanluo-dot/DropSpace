using DropSpace.Core.Content;
using DropSpace.Core.DragDrop;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class ContentAndDragPolicyTests
{
    [TestMethod]
    public void ImageContentPolicy_RecognizesRepositoryShapedImageFiles()
    {
        Assert.AreEqual(
            ItemContentType.Image,
            ItemContentPolicy.Infer(ItemKind.File, ".PNG", "image/png"));
        Assert.IsTrue(ItemContentPolicy.IsImage(ItemKind.File, ".jpeg", null));
        Assert.AreEqual(".png", ItemContentPolicy.NormalizeExtension("photo.PNG"));
    }

    [TestMethod]
    public void OleClassification_RequiresActualAcceptanceOrMaterializationForVisualAuthorization()
    {
        var evidenceOnly = new OleFileDataClassification(
            OleFileDataKind.ShellItems,
            0,
            OlePreferredDropEffect.Copy,
            IsFileLikeEvidence: true,
            CanAcceptNow: false,
            CanMaterialize: false);
        var accepted = evidenceOnly with { CanAcceptNow = true };
        var materializable = evidenceOnly with { CanMaterialize = true };

        Assert.IsFalse(evidenceOnly.CanAuthorizeVisual);
        Assert.IsTrue(accepted.CanAuthorizeVisual);
        Assert.IsTrue(materializable.CanAuthorizeVisual);
    }

    [TestMethod]
    public void ItemSelectionResolver_UsesAllSelectedCardsOnlyWhenClickedCardIsSelected()
    {
        var clicked = Snapshot(Guid.NewGuid(), "clicked");
        var second = Snapshot(Guid.NewGuid(), "second");
        var outside = Snapshot(Guid.NewGuid(), "outside");

        var selected = ItemSelectionResolver.ForClickedItem(clicked, [clicked, second, second]);
        var fallback = ItemSelectionResolver.ForClickedItem(clicked, [outside]);

        Assert.AreEqual(2, selected.Items.Count);
        Assert.AreEqual(clicked.Id, selected.Items[0].Id);
        Assert.AreEqual(second.Id, selected.Items[1].Id);
        Assert.AreEqual(1, fallback.Items.Count);
        Assert.AreEqual(clicked.Id, fallback.Single.Id);
    }

    private static DropItemSnapshot Snapshot(Guid id, string title) => new(
        id,
        ItemKind.File,
        ItemStatus.Available,
        title,
        null,
        ".txt",
        1,
        "text/plain",
        null,
        null,
        1);
}
