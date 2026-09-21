using DropSpace.App.Services.Media;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaSessionNativeSmokeTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task RealSmtcSubscriptionDrainsBeforeDisableReturns()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var observed = new TaskCompletionSource<MediaSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, snapshot) => observed.TrySetResult(snapshot);
        await service.SetEnabledAsync(true);
        Assert.IsTrue(service.IsAvailable, service.AvailabilityReason);
        var snapshot = await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(snapshot.Artwork is null || snapshot.Artwork.Length <= 4 * 1024 * 1024);
        var filtered = new TaskCompletionSource<MediaSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, value) =>
        {
            if (value == MediaSessionSnapshot.Empty) filtered.TrySetResult(value);
        };
        service.SetAllowedSources([], restrictToList: true);
        await filtered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.PlayPauseAsync());
        Assert.AreEqual("No media session is available.", rejected.Message);
        await service.SetEnabledAsync(false);
        Assert.IsFalse(service.IsAvailable);
        Assert.AreEqual(MediaSessionSnapshot.Empty, service.Current);
        Assert.AreEqual(0, service.AvailableSources.Count);
        await service.SetEnabledAsync(true);
        Assert.IsTrue(service.IsAvailable, service.AvailabilityReason);
        await service.SetEnabledAsync(false);
    }
}
