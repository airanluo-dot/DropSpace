using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class OutputReservationLifecycleTests
{
    [TestMethod]
    public async Task CheckpointCancellationAndFailurePreserveOwnershipWhileRealCollisionsStillCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-output-checkpoint-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "output");
        var staging = Path.Combine(root, "private.partial");
        byte[] payload = [1, 2, 3];
        byte[] external = [4, 5, 6];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var reservations = new OutputReservationService();
        Task<OutputReservation>? pending = null;
        try
        {
            var reservation = await reservations.ReserveAsync(Guid.NewGuid(), directory, "file.bin");
            var marker = await File.ReadAllBytesAsync(reservation.MarkerPath);
            await File.WriteAllBytesAsync(staging, payload);
            var canceledCheckpoints = 0;
            pending = reservations.CommitTrackedAsync(reservation, staging, async current =>
            {
                Assert.AreEqual(reservation, current);
                canceledCheckpoints++;
                entered.TrySetResult();
                await release.Task;
            }, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            release.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, canceledCheckpoints);
            Assert.IsFalse(File.Exists(reservation.OutputPath), "Cancellation during the checkpoint must prevent publication.");
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(staging));
            CollectionAssert.AreEqual(marker, await File.ReadAllBytesAsync(reservation.MarkerPath));
            Assert.HasCount(1, Directory.GetFiles(directory), "Cancellation must preserve just the original owned marker.");

            var checkpointFailure = new IOException("controlled checkpoint write failure");
            var failedCheckpoints = 0;
            var observed = await Assert.ThrowsExactlyAsync<IOException>(() =>
                reservations.CommitTrackedAsync(reservation, staging, async current =>
                {
                    Assert.AreEqual(reservation, current);
                    failedCheckpoints++;
                    await File.WriteAllBytesAsync(current.OutputPath, external);
                    throw checkpointFailure;
                }));
            Assert.AreSame(checkpointFailure, observed, "A checkpoint failure must not be mistaken for a destination collision.");
            Assert.AreEqual(1, failedCheckpoints);
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(staging));
            CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(reservation.OutputPath));
            CollectionAssert.AreEqual(marker, await File.ReadAllBytesAsync(reservation.MarkerPath));
            Assert.HasCount(2, Directory.GetFiles(directory), "The callback failure must not allocate an alternate path or marker.");

            var checkpoints = new List<OutputReservation>();
            var committed = await reservations.CommitTrackedAsync(reservation, staging, current =>
            {
                checkpoints.Add(current);
                return Task.CompletedTask;
            });
            Assert.HasCount(1, checkpoints);
            Assert.AreEqual(reservation.TaskId, committed.TaskId);
            Assert.AreNotEqual(reservation.OutputPath, committed.OutputPath);
            Assert.AreEqual(checkpoints[^1].OutputPath, committed.OutputPath,
                "The successful output must be the reservation recorded by the checkpoint.");
            Assert.IsFalse(committed.CleanupPending);
            Assert.IsFalse(File.Exists(staging));
            Assert.IsFalse(File.Exists(reservation.MarkerPath));
            Assert.IsFalse(File.Exists(committed.MarkerPath));
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(committed.OutputPath));
            CollectionAssert.AreEqual(external, await File.ReadAllBytesAsync(reservation.OutputPath));
            Assert.HasCount(0, Directory.GetFiles(directory, "*.dropspace-reservation"));
        }
        finally
        {
            await cancellation.CancelAsync();
            release.TrySetResult();
            if (pending is not null)
            {
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
