using DropSpace.Core.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace DropSpace.Core.Tests;
[TestClass]
public sealed class AudioCaptureRecoveryPolicyTests
{
    [TestMethod]
    public void UnavailableCaptureRecoversWithoutTrackChange() =>
        Assert.IsTrue(AudioCaptureRecoveryPolicy.ShouldRecover(true, AudioCaptureMode.Unavailable, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
    [TestMethod]
    public void StalledPacketStreamRecovers() =>
        Assert.IsTrue(AudioCaptureRecoveryPolicy.ShouldRecover(true, AudioCaptureMode.ProcessLoopback, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6)));
    [TestMethod]
    public void FreshSilentPacketsDoNotReconnect() =>
        Assert.IsFalse(AudioCaptureRecoveryPolicy.ShouldRecover(true, AudioCaptureMode.ProcessLoopback, TimeSpan.FromMilliseconds(20), TimeSpan.FromHours(1)));
    [TestMethod]
    public void StoppedPlaybackNeverReconnects() =>
        Assert.IsFalse(AudioCaptureRecoveryPolicy.ShouldRecover(false, AudioCaptureMode.Stopped, TimeSpan.FromHours(1), TimeSpan.FromHours(1)));
    [TestMethod]
    public void FailedCaptureHasRetryCooldown() =>
        Assert.IsFalse(AudioCaptureRecoveryPolicy.ShouldRecover(true, AudioCaptureMode.Unavailable, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1)));
}
