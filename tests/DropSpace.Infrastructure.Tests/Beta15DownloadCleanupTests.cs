using System.Net;
using System.Net.Sockets;
using System.Text;
using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta15DownloadCleanupTests
{
    [TestMethod]
    public async Task CompletedTransferKeepsDeferredCleanupOutOfDownloadFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-Beta15-complete-" + Guid.NewGuid().ToString("N"));
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var address = $"http://127.0.0.1:{port}/";
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = Task.Run(async () =>
        {
            // The range qualification probe is rejected with 200; the sequential
            // fallback then receives the same tiny body through the real engine.
            for (var request = 0; request < 2; request++)
            {
                using var client = await server.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))) { }
                await send.Task;
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(new byte[] { 1, 2, 3 });
            }
        });
        var completed = new TaskCompletionSource<DownloadTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var repository = new DownloadTaskRepository(Path.Combine(root, "journal"));
            using var engine = new HttpRangeDownloader();
            await using var manager = new DownloadManager(engine, repository);
            manager.TaskChanged += (_, task) =>
            {
                if (task.State == DownloadTaskState.Completed && task.CleanupPending) completed.TrySetResult(task);
                if (task.State == DownloadTaskState.Failed) completed.TrySetException(new IOException(task.ErrorCode));
            };
            await manager.EnqueueAsync(address + "file", root, "file.bin");
            var task = manager.Tasks.Single();
            using (var held = new FileStream(task.OutputPath + ".dropspace-reservation", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                send.SetResult();
                var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(DownloadTaskState.Completed, result.State);
                Assert.IsNull(result.ErrorCode);
                Assert.IsTrue(result.CleanupPending);
                Assert.IsNotNull(result.CleanupErrorCode);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result.OutputPath));
                await responses.WaitAsync(TimeSpan.FromSeconds(5));
                await manager.RemoveHistoryAsync(task.Id);
                Assert.HasCount(0, manager.Tasks);
                var retained = (await repository.GetAllAsync()).Single();
                Assert.IsTrue(retained.HistoryRemovalPending);
                Assert.IsTrue(retained.CleanupPending);
                Assert.AreEqual(DownloadTaskState.Completed, retained.State);
                Assert.IsNull(retained.ErrorCode);
            }
            await manager.RetryDeferredCleanupAsync();
            Assert.HasCount(0, await repository.GetAllAsync());
            Assert.IsFalse(File.Exists(task.OutputPath + ".dropspace-reservation"));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(task.OutputPath));
        }
        finally
        {
            send.TrySetResult(); server.Stop();
            try { await responses; } catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException) { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task RemovedHistoryRetainsOwnedMarkerAcrossRestartAndDoesNotDeleteFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-Beta15-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var reservations = new OutputReservationService();
            var id = Guid.NewGuid();
            var reservation = await reservations.ReserveAsync(id, root, "file.bin");
            var staging = Path.Combine(root, "private.partial");
            await File.WriteAllBytesAsync(staging, [4, 5, 6]);
            using var engine = new HttpRangeDownloader();
            var repository = new DownloadTaskRepository(Path.Combine(root, "journal"));
            FileStream? held = new(reservation.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var committed = await reservations.CommitAsync(reservation, staging);
                Assert.IsTrue(committed.CleanupPending);
                await Assert.ThrowsAsync<IOException>(() => reservations.ReleaseAsync(reservation));
                await repository.UpsertAsync(new()
                {
                    Id = id, Request = new(id, "https://example.com/file", root, "file.bin"),
                    OutputPath = reservation.OutputPath, State = DownloadTaskState.Completed,
                    ErrorCode = "MarkerCleanupDeferred", // A Beta14 journal must migrate safely.
                });
                await using (var first = new DownloadManager(engine, repository))
                {
                    await first.RestoreAsync();
                    Assert.IsNull(first.Tasks.Single().ErrorCode);
                    await first.RemoveHistoryAsync(id);
                    Assert.HasCount(0, first.Tasks);
                    Assert.IsTrue((await repository.GetAllAsync()).Single().HistoryRemovalPending);
                }
                await using var restarted = new DownloadManager(engine, repository);
                await restarted.RestoreAsync();
                Assert.HasCount(0, restarted.Tasks, "Pending cleanup must not reappear as download history.");
                Assert.IsTrue((await repository.GetAllAsync()).Single().CleanupPending);
                var moved = Path.Combine(root, "moved.bin");
                File.Move(reservation.OutputPath, moved);
                held.Dispose(); held = null;
                await restarted.RetryDeferredCleanupAsync();
                Assert.HasCount(0, await repository.GetAllAsync());
                Assert.IsFalse(File.Exists(reservation.MarkerPath));
                CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(moved));
                var next = await reservations.ReserveAsync(Guid.NewGuid(), root, "file.bin");
                Assert.AreEqual(reservation.OutputPath, next.OutputPath, "A moved output must not leave a false reservation collision.");
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reservations.ReleaseAsync(reservation));
                Assert.IsTrue(File.Exists(next.MarkerPath), "A former task cannot release another task's reservation.");
                await reservations.ReleaseAsync(next);
            }
            finally { held?.Dispose(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
