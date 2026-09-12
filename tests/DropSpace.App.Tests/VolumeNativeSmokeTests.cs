using DropSpace.App.Services.Audio;
using DropSpace.App.Services.Volume;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static DropSpace.App.Services.Volume.VolumeInterop;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class VolumeNativeSmokeTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task ObservesRealEndpointChangeAndRestoresVolume()
    {
        await using var service = new WindowsVolumeActivityService(NullLogger<WindowsVolumeActivityService>.Instance);
        await service.SetEnabledAsync(true);
        Assert.IsTrue(service.IsAvailable, "A real render endpoint is required for this smoke.");
        var observed = new TaskCompletionSource<VolumeActivitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, value) => observed.TrySetResult(value);
        await Task.Run(() =>
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioEndpointVolume? endpoint = null;
            var initialized = ProcessLoopbackInterop.CoInitializeEx(0, 0) >= 0;
            float? original = null;
            var context = Guid.NewGuid();
            try
            {
                enumerator = (IMMDeviceEnumerator)(object)new DeviceEnumerator();
                enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                device.Activate(typeof(IAudioEndpointVolume).GUID, 0x17, 0, out var value);
                endpoint = (IAudioEndpointVolume)value;
                endpoint.GetMasterVolumeLevelScalar(out var level); original = level;
                var changed = level >= 0.5f ? level - 0.01f : level + 0.01f;
                endpoint.SetMasterVolumeLevelScalar(changed, context);
                var notification = observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Assert.AreEqual((int)Math.Round(changed * 100), notification.Percent);
            }
            finally
            {
                try { if (original is { } level) endpoint?.SetMasterVolumeLevelScalar(level, context); }
                finally
                {
                    ProcessLoopbackInterop.Release(endpoint); ProcessLoopbackInterop.Release(device); ProcessLoopbackInterop.Release(enumerator);
                    if (initialized) ProcessLoopbackInterop.CoUninitialize();
                }
            }
        });
        await service.SetEnabledAsync(false);
        Assert.IsFalse(service.IsAvailable);
        Assert.IsNull(service.Current);
        await service.SetEnabledAsync(true);
        Assert.IsTrue(service.IsAvailable);
        await service.SetEnabledAsync(false);
    }
}
