using DropSpace.Core.Policies;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class RepeatedCaptureRetryAuditTests
{
    [TestMethod]
    public async Task EarlierSuccessfulRunDoesNotSuppressRetryAfterTheCurrentRunFails()
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        await CaptureAsync(coordinator, "A", true);
        await CaptureAsync(coordinator, "B", false);
        await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ExecuteAsync(
            "A", _ => Task.FromException<bool>(new IOException("repository unavailable")), value => value));

        var retried = false;
        var retry = await coordinator.ExecuteAsync("A", _ =>
        {
            retried = true;
            return Task.FromResult(true);
        }, value => value);

        Assert.IsFalse(retry.Suppressed);
        Assert.IsTrue(retried);
    }

    [TestMethod]
    public async Task EarlierSuccessfulRunDoesNotSuppressRetryAfterTheCurrentRunIsRejected()
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        await CaptureAsync(coordinator, "A", true);
        await CaptureAsync(coordinator, "B", false);
        await CaptureAsync(coordinator, "A", false);

        var retry = await CaptureAsync(coordinator, "A", true);

        Assert.IsFalse(retry.Suppressed);
        Assert.IsTrue(retry.Value);
    }

    private static Task<ConsecutiveClipboardCaptureResult<bool>> CaptureAsync(
        ConsecutiveClipboardCaptureCoordinator coordinator, string fingerprint, bool persisted) =>
        coordinator.ExecuteAsync(fingerprint, _ => Task.FromResult(persisted), value => value);
}
