using DropSpace.Infrastructure.Downloads;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DownloadBandwidthSettingsTests
{
    [TestMethod]
    public async Task ReapplyingSameLimitCannotRefillSpentDownloadTokens()
    {
        // Freeze elapsed time so this checks settings publication, not wall-clock timing.
        var limiter = new DownloadBandwidthLimiter(new FrozenClock());
        limiter.SetLimit(1);
        await limiter.ConsumeAsync(1, CancellationToken.None);
        limiter.SetLimit(1);
        using var cancellation = new CancellationTokenSource();
        var pending = limiter.ConsumeAsync(1, cancellation.Token).AsTask();
        try
        {
            Assert.IsFalse(pending.IsCompleted,
                "Applying unchanged settings must not grant a fresh burst to every active download.");
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(20L)]
    public async Task ChangedLimitStillWakesBlockedDownload(long newLimit)
    {
        var limiter = new DownloadBandwidthLimiter(new FrozenClock());
        limiter.SetLimit(1);
        await limiter.ConsumeAsync(1, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = limiter.ConsumeAsync(1, cancellation.Token).AsTask();
        try
        {
            Assert.IsFalse(pending.IsCompleted);
            limiter.SetLimit(newLimit);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(newLimit, limiter.BytesPerSecond);
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }
}
