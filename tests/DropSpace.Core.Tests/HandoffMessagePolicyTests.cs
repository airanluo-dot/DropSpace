using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Transfer;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class HandoffMessagePolicyTests
{
    [TestMethod]
    public void TextAndUrlMessagesAreBoundedNormalizedAndIntegrityChecked()
    {
        var sender = Guid.NewGuid();
        var text = HandoffMessagePolicy.Create(sender, "Windows A", HandoffMessageKind.Text, "line 1\r\nline 2");
        Assert.AreEqual("line 1\nline 2", text.Utf8Payload);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(text.Utf8Payload), text.ByteLength);
        HandoffMessagePolicy.Validate(text);

        var url = HandoffMessagePolicy.Create(sender, "Windows A", HandoffMessageKind.Url, "https://example.test/path#fragment");
        Assert.AreEqual("https://example.test/path", url.Utf8Payload);
        HandoffMessagePolicy.Validate(url);

        Assert.ThrowsExactly<InvalidDataException>(() => HandoffMessagePolicy.Validate(text with { Sha256 = "not-a-hash" }));
        Assert.ThrowsExactly<InvalidDataException>(() => HandoffMessagePolicy.Validate(text with { ByteLength = text.ByteLength + 1 }));
    }

    [TestMethod]
    public void TextAndUrlLimitsAreMeasuredInUtf8Bytes()
    {
        var sender = Guid.NewGuid();
        var textTooLarge = new string('x', HandoffMessagePolicy.MaximumTextBytes + 1);
        Assert.ThrowsExactly<InvalidDataException>(() => HandoffMessagePolicy.Create(sender, "Windows A", HandoffMessageKind.Text, textTooLarge));

        var urlTooLarge = string.Concat("https://example.test/", new string('x', HandoffMessagePolicy.MaximumUrlBytes));
        Assert.ThrowsExactly<InvalidDataException>(() => HandoffMessagePolicy.Create(sender, "Windows A", HandoffMessageKind.Url, urlTooLarge));
    }
}
