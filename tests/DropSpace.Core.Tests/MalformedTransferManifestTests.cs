using DropSpace.Core.Transfer;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class MalformedTransferManifestTests
{
    [TestMethod]
    [DataRow("null-item")]
    [DataRow("null-path")]
    [DataRow("null-hash")]
    [DataRow("undefined-kind")]
    public void MalformedWireItemsAreRejectedAsInvalidData(string corruption)
    {
        var item = new TransferItemManifest(Guid.NewGuid(), TransferItemKind.File, "file.txt",
            "file.txt", 1, new string('a', 64), "text/plain", 1);
        item = corruption switch
        {
            "null-item" => null!,
            "null-path" => item with { RelativePath = null! },
            "null-hash" => item with { Sha256 = null! },
            _ => item with { Kind = (TransferItemKind)1234 },
        };
        var manifest = new TransferManifest(Guid.NewGuid(), DropLinkProtocolVersion.V1, [item],
            1, DateTimeOffset.UtcNow);

        Assert.ThrowsExactly<InvalidDataException>(() => TransferManifestPolicy.Validate(manifest));
    }
}
