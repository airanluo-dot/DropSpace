using System.Text.Json;
using DropSpace.Core.Policies;
using DropSpace.Core.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class CoreBehaviorTests
{
    [TestMethod]
    public void PayloadPathCannotEscapeStorageRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", "payloads");

        Assert.ThrowsExactly<InvalidDataException>(() =>
            PayloadPathPolicy.ResolveContainedPath(root, Path.Combine("..", "outside.bin")));
    }

    [TestMethod]
    public void LegacyPreviewChannelIsReadAsBeta()
    {
        var channel = JsonSerializer.Deserialize<UpdateChannel>("\"Preview\"");

        Assert.AreEqual(UpdateChannel.Beta, channel);
        Assert.AreEqual("\"Beta\"", JsonSerializer.Serialize(channel));
    }
}
