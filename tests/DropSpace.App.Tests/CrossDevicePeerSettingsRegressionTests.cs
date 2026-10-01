using System.Reflection;
using DropSpace.App.Services;
using DropSpace.Core.Models;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class CrossDevicePeerSettingsRegressionTests
{
    [TestMethod]
    public async Task StaleRuntimeSettingsCannotForgetNewlyPersistedPeerModes()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-peer-settings", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonSettingsService(new AppStoragePaths(root));
            var original = new AppSettings { EnableCrossDeviceClipboard = true };
            var peerId = Guid.NewGuid();
            await store.SaveAsync(original with
            {
                ClipboardPeerModes = new Dictionary<Guid, ClipboardSyncMode> { [peerId] = ClipboardSyncMode.AutomaticTextAndUrl },
            });
            // This exercises the already-started settings path, without native capture,
            // identities or a propagation worker; no native resources are constructed.
            var service = new CrossDeviceClipboardService(null!, null!, null!, null!, null!, store, null!,
                NullLogger<CrossDeviceClipboardService>.Instance);
            typeof(CrossDeviceClipboardService).GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, true);
            await service.UpdateSettingsAsync(original);
            var applied = (AppSettings)typeof(CrossDeviceClipboardService).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            Assert.AreEqual(ClipboardSyncMode.AutomaticTextAndUrl, applied.ClipboardPeerModes[peerId]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
