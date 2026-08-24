using DropSpace.Core.Displays;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class DisplayIdentityTests
{
    [TestMethod]
    public void DevicePathNormalizationProducesTheSamePersistentId()
    {
        var first = DisplayIdentity.CreatePersistentId("\\\\?\\DISPLAY#DEL4098#5&1A2B3C4D&0&UID4352#{GUID}");
        var second = DisplayIdentity.CreatePersistentId("  //?//display#del4098#5&1a2b3c4d&0&uid4352#{guid}  ");

        Assert.AreEqual(first, second);
        Assert.IsTrue(DisplayIdentity.IsPersistentId(first));
    }

    [TestMethod]
    public void RuntimeFallbackIsExplicitlyDistinguishableFromPersistentIdentity()
    {
        var fallback = DisplayIdentity.CreateRuntimeFallbackId(new nint(0x1234));

        Assert.AreEqual("runtime:1234", fallback);
        Assert.IsFalse(DisplayIdentity.IsPersistentId(fallback));
    }
}
