using System.Runtime.InteropServices;
using System.Text;
using DropSpace.App.Services;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class FullAuditVirtualDescriptorRegressionTests
{
    [TestMethod]
    public void IncompleteHeaderIsRejectedBeforeReadingNativeMemory() =>
        Assert.ThrowsExactly<InvalidDataException>(() => VirtualFileMaterializer.ReadDescriptorsFromMemory(new nint(1), 3));

    [TestMethod]
    public void MissingDescriptorIsRejectedBeforeReadingNativeMemory()
    {
        var memory = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(memory, 1);
            Assert.ThrowsExactly<InvalidDataException>(() => VirtualFileMaterializer.ReadDescriptorsFromMemory(memory, 4));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [TestMethod]
    public void UnannouncedSizeFieldsAreIgnored()
    {
        var descriptors = Parse(0, uint.MaxValue, uint.MaxValue);
        Assert.AreEqual("payload.txt", descriptors[0].FileName);
        Assert.AreEqual(0L, descriptors[0].AnnouncedSize);
    }

    [TestMethod]
    public void AnnouncedSizeIsEnforced()
    {
        Assert.AreEqual(3L, Parse(0x40, 0, 3)[0].AnnouncedSize);
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(0x40, 0, 2_147_483_649));
    }

    private static IReadOnlyList<VirtualFileMaterializer.VirtualFileDescriptor> Parse(uint flags, uint high, uint low)
    {
        var bytes = new byte[4 + 592];
        BitConverter.GetBytes(1).CopyTo(bytes, 0);
        BitConverter.GetBytes(flags).CopyTo(bytes, 4);
        BitConverter.GetBytes(high).CopyTo(bytes, 68);
        BitConverter.GetBytes(low).CopyTo(bytes, 72);
        Encoding.Unicode.GetBytes("payload.txt").CopyTo(bytes, 76);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            return VirtualFileMaterializer.ReadDescriptorsFromMemory(memory, bytes.Length);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
