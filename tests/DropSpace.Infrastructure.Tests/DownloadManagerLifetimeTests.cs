using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DownloadManagerLifetimeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public async Task LateRestorePreservesAdmittedRunOwnershipAndStillRecoversNewTasks()
    {
        await using var fixture = new DownloadFixture(startServer: false);
        using var transfer = await fixture.Engine.Transfers.AcquireAsync(default);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object? work = null;
        CancellationTokenSource? source = null;
        Task? run = null;
        EventHandler<DownloadTaskSnapshot> observer = (_, snapshot) =>
        {
            if (snapshot.Request.OutputFileName == "live.bin" && snapshot.Stage == DownloadStage.Queued)
                queued.TrySetResult();
        };
        fixture.Manager.TaskChanged += observer;
        try
        {
            await fixture.Manager.EnqueueAsync(fixture.Url, fixture.Root, "live.bin");
            var live = fixture.Manager.Tasks.Single();
            work = GetWork(fixture.Manager, live.Id);
            source = GetSource(work);
            run = GetRun(work);
            var reservation = work.GetType().GetField("Reservation")!.GetValue(work);
            Assert.IsNotNull(source);
            Assert.IsNotNull(reservation);
            await queued.Task.WaitAsync(Budget);
            Assert.IsFalse(run.IsCompleted, "The original run must still own its queued transfer.");
            Assert.AreEqual(1, fixture.Engine.Transfers.Active);
            Assert.AreEqual(0, fixture.Engine.Connections.Active);

            var coldRequest = DirectFileRequestFactory.Create(fixture.Url, fixture.Root, "cold.bin");
            await new DownloadTaskRepository(Path.Combine(fixture.Root, "journal")).UpsertAsync(new()
            {
                Id = coldRequest.TaskId, Request = coldRequest,
                OutputPath = Path.Combine(fixture.Root, coldRequest.OutputFileName),
                State = DownloadTaskState.DownloadingFile, RunId = 4, ActiveConnections = 3, BytesPerSecond = 123,
            });
            await fixture.Manager.RestoreAsync().WaitAsync(Budget);

            Assert.AreSame(work, GetWork(fixture.Manager, live.Id), "Recovery replaced a live admission owner.");
            Assert.AreSame(source, GetSource(work), "Recovery lost the original run's cancellation source.");
            Assert.AreSame(run, GetRun(work), "Recovery lost the original run task.");
            Assert.AreSame(reservation, work.GetType().GetField("Reservation")!.GetValue(work),
                "Recovery replaced the live output reservation.");
            Assert.HasCount(2, fixture.Manager.Tasks);
            var cold = fixture.Manager.Tasks.Single(item => item.Id == coldRequest.TaskId);
            Assert.AreEqual(DownloadTaskState.Paused, cold.State);
            Assert.AreEqual(0, cold.ActiveConnections);
            Assert.AreEqual(0d, cold.BytesPerSecond);
            Assert.IsNull(GetSource(GetWork(fixture.Manager, cold.Id)));
            Assert.IsTrue(GetRun(GetWork(fixture.Manager, cold.Id)).IsCompleted,
                "Cold recovery must not start a network transfer.");

            await fixture.Manager.CancelAsync(live.Id).WaitAsync(Budget);
            Assert.IsTrue(run.IsCompleted, "Cancel returned while the original queued run was still active.");
            AssertReleased(work, source);
            Assert.AreEqual(DownloadTaskState.Cancelled, fixture.Manager.Tasks.Single(item => item.Id == live.Id).State);
            Assert.AreEqual(DownloadTaskState.Paused, fixture.Manager.Tasks.Single(item => item.Id == cold.Id).State);
            Assert.IsFalse(File.Exists(live.OutputPath));
            Assert.IsFalse(File.Exists(live.OutputPath + ".dropspace-reservation"));
            Assert.IsFalse(File.Exists(cold.OutputPath));
            Assert.AreEqual(1, fixture.Engine.Transfers.Active, "Only the fixture's held transfer slot remains active.");
            Assert.AreEqual(0, fixture.Engine.Connections.Active, "No HTTP connection should have been admitted.");
        }
        finally
        {
            fixture.Manager.TaskChanged -= observer;
            // Even a regression that orphans the original Work must not release
            // the held transfer slot until that exact old run has been canceled.
            if (run is { IsCompleted: false })
            {
                try { if (source is not null) await source.CancelAsync(); }
                catch (ObjectDisposedException) { /* The original run just retired. */ }
                await run.WaitAsync(Budget);
            }
        }
    }

    [TestMethod]
    public async Task CompletedRunsReleaseCancellationSourcesAndKeepDeliveredHistory()
    {
        await using var fixture = new DownloadFixture();
        for (var index = 0; index < 12; index++)
        {
            object work;
            CancellationTokenSource source;
            using (await fixture.Engine.Transfers.AcquireAsync(default))
            {
                await fixture.Manager.EnqueueAsync(fixture.Url, fixture.Root, $"file-{index}.bin");
                work = GetWork(fixture.Manager, fixture.Manager.Tasks.Last().Id);
                source = GetSource(work)!;
                Assert.IsNotNull(source);
            }
            await GetRun(work).WaitAsync(Budget);
            AssertReleased(work, source);
        }
        Assert.HasCount(12, fixture.Manager.Tasks);
        Assert.IsTrue(fixture.Manager.Tasks.All(item => item.State == DownloadTaskState.Completed));
        foreach (var task in fixture.Manager.Tasks)
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(task.OutputPath));
    }

    [TestMethod]
    public async Task FailureRetryAndQueuedPauseResumeCancelKeepPerRunOwnership()
    {
        await using var fixture = new DownloadFixture();
        fixture.Status = HttpStatusCode.NotFound;
        object work;
        CancellationTokenSource failedSource;
        using (await fixture.Engine.Transfers.AcquireAsync(default))
        {
            await fixture.Manager.EnqueueAsync(fixture.Url, fixture.Root, "retry.bin");
            work = GetWork(fixture.Manager, fixture.Manager.Tasks.Single().Id);
            failedSource = GetSource(work)!;
        }
        await GetRun(work).WaitAsync(Budget);
        Assert.AreEqual(DownloadTaskState.Failed, fixture.Manager.Tasks.Single().State);
        AssertReleased(work, failedSource);
        var failedId = fixture.Manager.Tasks.Single().Id;

        fixture.Status = HttpStatusCode.OK;
        CancellationTokenSource retrySource;
        using (await fixture.Engine.Transfers.AcquireAsync(default))
        {
            await fixture.Manager.ResumeAsync(failedId);
            retrySource = GetSource(work)!;
            Assert.AreNotSame(failedSource, retrySource);
        }
        await GetRun(work).WaitAsync(Budget);
        Assert.AreEqual(DownloadTaskState.Completed, fixture.Manager.Tasks.Single().State);
        AssertReleased(work, retrySource);
        var delivered = fixture.Manager.Tasks.Single().OutputPath;

        Guid pausedId;
        using (await fixture.Engine.Transfers.AcquireAsync(default))
        {
            await fixture.Manager.EnqueueAsync(fixture.Url, fixture.Root, "paused.bin");
            pausedId = fixture.Manager.Tasks.Last().Id;
            var pausedWork = GetWork(fixture.Manager, pausedId);
            var pausedSource = GetSource(pausedWork)!;
            await fixture.Manager.PauseAsync(pausedId).WaitAsync(Budget);
            Assert.AreEqual(DownloadTaskState.Paused, fixture.Manager.Tasks.Last().State);
            AssertReleased(pausedWork, pausedSource);

            await fixture.Manager.ResumeAsync(pausedId);
            var resumedSource = GetSource(pausedWork)!;
            Assert.AreNotSame(pausedSource, resumedSource);
            await fixture.Manager.CancelAsync(pausedId).WaitAsync(Budget);
            Assert.AreEqual(DownloadTaskState.Cancelled, fixture.Manager.Tasks.Last().State);
            AssertReleased(pausedWork, resumedSource);
        }
        Assert.HasCount(2, fixture.Manager.Tasks);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "paused.bin")));
        await fixture.Manager.RemoveHistoryAsync(pausedId);
        Assert.HasCount(1, fixture.Manager.Tasks);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(delivered));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunRetirementWaitsForExplicitAndParentCancellationCallbacks(bool shutdown)
    {
        await using var fixture = new DownloadFixture();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceReady = new TaskCompletionSource<CancellationTokenSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        object? work = null;
        EventHandler<DownloadTaskSnapshot> observer = (_, snapshot) =>
        {
            // Initial Queued publication precedes the transport's own token registrations.
            // Its later progress publication proves the transfer is waiting on the held slot.
            if (snapshot.State == DownloadTaskState.Queued && work is null)
            {
                work = GetWork(fixture.Manager, snapshot.Id);
                var source = GetSource(work)!;
                registration = source.Token.UnsafeRegister(_ =>
                {
                    callbackEntered.TrySetResult();
                    if (!releaseCallback.Wait(Budget)) throw new TimeoutException("Callback test gate was not released.");
                    _ = source.Token; // The callback still owns a live source until it exits.
                }, null);
                sourceReady.TrySetResult(source);
            }
            if (snapshot.State == DownloadTaskState.Queued && snapshot.Stage == DownloadStage.Queued) queued.TrySetResult();
            if (snapshot.State == DownloadTaskState.Paused) paused.TrySetResult();
        };
        fixture.Manager.TaskChanged += observer;
        try
        {
            using (await fixture.Engine.Transfers.AcquireAsync(default))
            {
                await fixture.Manager.EnqueueAsync(fixture.Url, fixture.Root, "held.bin");
                var source = await sourceReady.Task.WaitAsync(Budget);
                await queued.Task.WaitAsync(Budget);
                var run = GetRun(work!);
                var cancel = shutdown ? fixture.Manager.ShutdownAsync() : fixture.Manager.PauseAsync(fixture.Manager.Tasks.Single().Id);
                await callbackEntered.Task.WaitAsync(Budget);
                var overlappingShutdown = shutdown ? fixture.Manager.ShutdownAsync() : Task.CompletedTask;
                await paused.Task.WaitAsync(Budget);
                Assert.IsTrue(await Task.Run(() => SpinWait.SpinUntil(() => GetSource(work!) is null || run.IsCompleted, Budget)),
                    "The settled transfer must reach retirement independently of its pending callback.");
                Assert.IsFalse(run.IsCompleted, "A run remains owned while a cancellation callback is executing.");
                _ = source.Token;
                releaseCallback.Set();
                await cancel.WaitAsync(Budget);
                await overlappingShutdown.WaitAsync(Budget);
                await run.WaitAsync(Budget);
                AssertReleased(work!, source);
                Assert.AreEqual(DownloadTaskState.Paused, fixture.Manager.Tasks.Single().State);
            }
        }
        finally
        {
            releaseCallback.Set();
            fixture.Manager.TaskChanged -= observer;
            registration.Dispose();
        }
    }

    [TestMethod]
    public async Task CancellationCallbackFailuresReachCallersAndStillAllowRetirementAndCleanup()
    {
        await using var fixture = new DownloadFixture();
        object work;
        Guid id;
        using (await fixture.Engine.Transfers.AcquireAsync(default))
        {
            await fixture.Manager.EnqueueAsync(fixture.Url, fixture.Root, "retry-after-callback.bin");
            id = fixture.Manager.Tasks.Single().Id;
            work = GetWork(fixture.Manager, id);
            var source = GetSource(work)!;
            using var registration = source.Token.UnsafeRegister(_ => throw new InvalidOperationException("fixture-explicit-callback"), null);
            var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Manager.PauseAsync(id));
            StringAssert.Contains(failure.ToString(), "fixture-explicit-callback");
            await GetRun(work).WaitAsync(Budget);
            AssertReleased(work, source);
            Assert.AreEqual(DownloadTaskState.Paused, fixture.Manager.Tasks.Single().State);
        }
        await fixture.Manager.ResumeAsync(id);
        await GetRun(work).WaitAsync(Budget);
        Assert.AreEqual(DownloadTaskState.Completed, fixture.Manager.Tasks.Single().State);

        using (await fixture.Engine.Transfers.AcquireAsync(default))
        {
            await fixture.Manager.EnqueueAsync(fixture.Url, fixture.Root, "parent-callback.bin");
            var parentWork = GetWork(fixture.Manager, fixture.Manager.Tasks.Last().Id);
            var source = GetSource(parentWork)!;
            using var registration = source.Token.UnsafeRegister(_ => throw new InvalidOperationException("fixture-parent-callback"), null);
            var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Manager.ShutdownAsync());
            StringAssert.Contains(failure.ToString(), "fixture-parent-callback");
            AssertReleased(parentWork, source);
            Assert.AreEqual(DownloadTaskState.Paused, fixture.Manager.Tasks.Last().State);
            var persistence = (DownloadPersistenceWorker)typeof(DownloadManager).GetField("_persistence", PrivateInstance)!.GetValue(fixture.Manager)!;
            Assert.IsFalse(persistence.IsAccepting, "Cancellation errors must surface after the journal writer has drained.");

            var disposalFailure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Manager.DisposeAsync().AsTask());
            StringAssert.Contains(disposalFailure.ToString(), "fixture-parent-callback");
            var lifetime = (CancellationTokenSource)typeof(DownloadManager).GetField("_lifetime", PrivateInstance)!.GetValue(fixture.Manager)!;
            Assert.Throws<ObjectDisposedException>(() => _ = lifetime.Token);
            foreach (var name in new[] { "_progressNotification", "_cleanupRetry" })
            {
                var timer = (Timer)typeof(DownloadManager).GetField(name, PrivateInstance)!.GetValue(fixture.Manager)!;
                Assert.IsFalse(timer.Change(Timeout.Infinite, Timeout.Infinite), "Disposed timers must reject rescheduling.");
            }
        }
    }

    private static object GetWork(DownloadManager manager, Guid id) =>
        ((IDictionary)typeof(DownloadManager).GetField("_work", PrivateInstance)!.GetValue(manager)!)[id]!;
    private static CancellationTokenSource? GetSource(object work) =>
        (CancellationTokenSource?)work.GetType().GetField("Stop")!.GetValue(work);
    private static Task GetRun(object work) => (Task)work.GetType().GetField("Run")!.GetValue(work)!;
    private static void AssertReleased(object work, CancellationTokenSource source)
    {
        Assert.IsNull(GetSource(work), "History must not retain the settled run's cancellation owner.");
        Assert.Throws<ObjectDisposedException>(() => _ = source.Token);
    }

    private sealed class DownloadFixture : IAsyncDisposable
    {
        private readonly TcpListener _server = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _responses;
        private int _status = (int)HttpStatusCode.OK;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DropSpace-download-lifetime-" + Guid.NewGuid().ToString("N"));
        public HttpRangeDownloader Engine { get; } = new();
        public DownloadManager Manager { get; }
        public string Url { get; }
        public HttpStatusCode Status { get => (HttpStatusCode)Volatile.Read(ref _status); set => Volatile.Write(ref _status, (int)value); }
        public DownloadFixture(bool startServer = true)
        {
            Directory.CreateDirectory(Root);
            Engine.Transfers.SetLimit(1);
            Manager = new(Engine, new DownloadTaskRepository(Path.Combine(Root, "journal")));
            if (!startServer)
            {
                // The restore scenario holds the sole transfer slot through its
                // original run's settlement; it needs no HTTP endpoint or listener.
                Url = "http://127.0.0.1:1/not-requested";
                _responses = Task.CompletedTask;
                return;
            }
            _server.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_server.LocalEndpoint).Port}/file";
            _responses = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _server.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    var status = Volatile.Read(ref _status);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: 3\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(new byte[] { 1, 2, 3 }, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            Engine.Dispose();
            _stop.Cancel();
            await _responses;
            _server.Stop();
            _stop.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
