using System.Text.Json;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ClipboardWireBudgetTests
{
    [TestMethod]
    [DataRow(13 * 1024 * 1024)]
    [DataRow(50 * 1024 * 1024)]
    public void EntireLegalImageEnvelopeFitsClipboardWireBudget(int imageBytes)
    {
        var bytes = new byte[imageBytes];
        var envelope = new ClipboardEnvelope(Guid.NewGuid(), Guid.NewGuid(), 1,
            ClipboardPayloadKind.Image, DateTimeOffset.UtcNow, new string('a', 64), imageBytes,
            "image/png", null, bytes);
        var body = JsonSerializer.SerializeToUtf8Bytes(new ClipboardSyncRequest(envelope.OriginDeviceId, envelope),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsTrue(body.Length <= DropLinkProtocolPolicy.BodyLimitFor(DropLinkProtocolRoutes.Clipboard));
        Assert.IsTrue(body.Length > DropLinkProtocolPolicy.MaximumAuthenticatedBodyBytes,
            "This regression must exercise the previously rejected range.");
    }

    [TestMethod]
    public void ClipboardAliasesUseLargeBoundButOtherRoutesKeepTheirOriginalBound()
    {
        Assert.AreEqual(DropLinkProtocolPolicy.MaximumClipboardBodyBytes,
            DropLinkProtocolPolicy.BodyLimitFor(DropLinkProtocolRoutes.Clipboard.ToUpperInvariant() + "/"));
        Assert.AreEqual(DropLinkProtocolPolicy.MaximumAuthenticatedBodyBytes,
            DropLinkProtocolPolicy.BodyLimitFor(DropLinkProtocolRoutes.HandoffText));
        Assert.AreEqual(DropLinkProtocolPolicy.MaximumAuthenticatedBodyBytes,
            DropLinkProtocolPolicy.BodyLimitFor(DropLinkProtocolRoutes.Clipboard + "-other"));
    }
}
