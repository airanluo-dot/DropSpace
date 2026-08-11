using DropSpace.Core.Policies;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class ConsecutiveClipboardCaptureCoordinatorTests
{
    [TestMethod]
    public async Task ConsecutiveSuccessfulSnapshotsAreCollapsed()
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        var commits = 0;

        var first = await CaptureAsync(coordinator, "A", () => ++commits);
        var second = await CaptureAsync(coordinator, "A", () => ++commits);

        Assert.IsFalse(first.Suppressed);
        Assert.IsTrue(second.Suppressed);
        Assert.AreEqual(1, commits);
    }

    [TestMethod]
    public async Task NonConsecutiveSnapshotsArePreserved()
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        var persisted = new List<string>();

        foreach (var fingerprint in new[] { "A", "B", "A" })
        {
            await coordinator.ExecuteAsync(
                fingerprint,
                _ =>
                {
                    persisted.Add(fingerprint);
                    return Task.FromResult(true);
                },
                value => value);
        }

        CollectionAssert.AreEqual(new[] { "A", "B", "A" }, persisted);
    }

    [TestMethod]
    public async Task ConsecutiveDuplicate_IsSuppressed_ButNonConsecutiveDuplicateIsStored()
    {
        await AssertSequenceAsync(["A", "B", "A"], ["A", "A", "B", "A"]);
    }

    [TestMethod]
    public async Task RequiredClipboardSequencesCollapseOnlyAdjacentRuns()
    {
        await AssertSequenceAsync(["A"], ["A", "A"]);
        await AssertSequenceAsync(["A"], ["A", "A", "A", "A"]);
        await AssertSequenceAsync(["A", "B", "A"], ["A", "B", "A"]);
        await AssertSequenceAsync(["A", "B", "A"], ["A", "A", "B", "B", "A", "A"]);
        await AssertSequenceAsync(["A", "B", "C", "A"], ["A", "B", "B", "B", "C", "C", "A"]);
        await AssertSequenceAsync(["A", "AA"], ["A", "AA"]);
    }

    [TestMethod]
    public async Task FailedCommitDoesNotPoisonTheNextAttempt()
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ExecuteAsync(
            "image-fingerprint",
            _ => Task.FromException<bool>(new IOException("simulated repository failure")),
            value => value));

        var retry = await CaptureAsync(coordinator, "image-fingerprint", () => 1);

        Assert.IsFalse(retry.Suppressed);
        Assert.AreEqual(1, retry.Value);
    }

    [TestMethod]
    public async Task PolicyRejectedObservationSeparatesOtherwiseEqualSnapshots()
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        var persisted = new List<string>();
        await CaptureAsync(coordinator, "A", () => { persisted.Add("A"); return 1; });
        await CaptureAsync(coordinator, "rejected-B", () => 0);
        await CaptureAsync(coordinator, "A", () => { persisted.Add("A"); return 1; });

        CollectionAssert.AreEqual(new[] { "A", "A" }, persisted);
    }

    [TestMethod]
    public async Task ClearHistoryResetAllowsTheSameSnapshotAgain()
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        var commits = 0;
        await CaptureAsync(coordinator, "file-batch", () => ++commits);
        Assert.IsTrue((await CaptureAsync(coordinator, "file-batch", () => ++commits)).Suppressed);

        await coordinator.ResetAsync();
        var afterClear = await CaptureAsync(coordinator, "file-batch", () => ++commits);

        Assert.IsFalse(afterClear.Suppressed);
        Assert.AreEqual(2, commits);
    }

    [TestMethod]
    public async Task ConcurrentNotificationsStillCommitOnlyOnce()
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        var commits = 0;
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => coordinator.ExecuteAsync(
                "same-text",
                async cancellationToken =>
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Interlocked.Increment(ref commits);
                },
                _ => true))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.AreEqual(1, commits);
        Assert.AreEqual(31, results.Count(result => result.Suppressed));
    }

    [TestMethod]
    public async Task TextImageSingleFileAndMultiFileFingerprintsUseTheSameRule()
    {
        var identities = new[]
        {
            (A: FingerprintService.ForText("clipboard text A"), B: FingerprintService.ForText("clipboard text B")),
            (A: FingerprintService.ForBytes([1, 2, 3, 4]), B: FingerprintService.ForBytes([5, 6, 7, 8])),
            (A: FingerprintService.ForText("clipboard-file\0C:\\one.txt"), B: FingerprintService.ForText("clipboard-file\0C:\\two.txt")),
            (A: FingerprintService.ForText("C:\\one.txt\nC:\\two.txt"), B: FingerprintService.ForText("C:\\one.txt\nC:\\three.txt")),
        };

        foreach (var identity in identities)
        {
            await AssertSequenceAsync([identity.A], [identity.A, identity.A]);
            await AssertSequenceAsync([identity.A, identity.B, identity.A], [identity.A, identity.B, identity.A]);
        }
    }

    [TestMethod]
    public async Task NewProcessCoordinatorDoesNotCarryStaleSuppressionState()
    {
        using (var firstProcess = new ConsecutiveClipboardCaptureCoordinator())
        {
            await CaptureAsync(firstProcess, "A", () => 1);
        }

        using var restartedProcess = new ConsecutiveClipboardCaptureCoordinator();
        var capture = await CaptureAsync(restartedProcess, "A", () => 1);

        Assert.IsFalse(capture.Suppressed);
    }

    private static Task<ConsecutiveClipboardCaptureResult<int>> CaptureAsync(
        ConsecutiveClipboardCaptureCoordinator coordinator,
        string fingerprint,
        Func<int> commit) =>
        coordinator.ExecuteAsync(
            fingerprint,
            _ => Task.FromResult(commit()),
            value => value > 0);

    private static async Task AssertSequenceAsync(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> observed)
    {
        using var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        var persisted = new List<string>();
        foreach (var fingerprint in observed)
        {
            await coordinator.ExecuteAsync(
                fingerprint,
                _ =>
                {
                    persisted.Add(fingerprint);
                    return Task.FromResult(true);
                },
                value => value);
        }

        CollectionAssert.AreEqual(expected.ToArray(), persisted.ToArray());
    }
}
