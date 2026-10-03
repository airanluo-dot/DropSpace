using System.Text.Json;
using DropSpace.Core.Models;
using DropSpace.Core.Transfer;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class ClipboardPeerSettingsTests
{
    [TestMethod]
    public void ExplicitPeerModesPersistAndOldSettingsDoNotEnableUnknownPeers()
    {
        var id = Guid.NewGuid();
        var settings = new AppSettings { ClipboardPeerModes = new Dictionary<Guid, ClipboardSyncMode> { [id] = ClipboardSyncMode.AutomaticTextAndUrl } };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!.Validate();
        Assert.AreEqual(ClipboardSyncMode.AutomaticTextAndUrl, restored.ClipboardPeerModes[id]);
        Assert.HasCount(0, JsonSerializer.Deserialize<AppSettings>("{}")!.Validate().ClipboardPeerModes);
    }

    [TestMethod]
    public void EmptyPeerIdentityAndUnknownModeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppSettings { ClipboardPeerModes = new Dictionary<Guid, ClipboardSyncMode> { [Guid.Empty] = ClipboardSyncMode.Manual } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AppSettings { ClipboardPeerModes = new Dictionary<Guid, ClipboardSyncMode> { [Guid.NewGuid()] = (ClipboardSyncMode)999 } }.Validate());
    }
}
