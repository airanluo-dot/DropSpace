using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DownloadPersistenceAdmissionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ConcurrentDisposalSharesRetirementAndSettlesAcceptedWrites()
    {
        var repository = new FixtureRepository(blockFirstWrite: true);
        await using var worker = new DownloadPersistenceWorker(repository);
        var first = worker.EnqueueCriticalAsync(Snapshot());
        await repository.WriteStarted.Task.WaitAsync(Budget);
        var second = worker.EnqueueCriticalAsync(Snapshot());
        try
        {
            var disposal = worker.DisposeAsync().AsTask();
            var concurrentDisposal = worker.DisposeAsync().AsTask();
            Assert.AreSame(disposal, concurrentDisposal, "Every disposer must observe the same retirement completion.");
            Assert.IsFalse(disposal.IsCompleted, "Disposal must retain the in-flight repository owner.");
            repository.ReleaseWrite.TrySetResult();
            await Task.WhenAll(first, second, disposal).WaitAsync(Budget);
            Assert.AreEqual(2, repository.WriteCount);
            Assert.AreSame(disposal, worker.DisposeAsync().AsTask());
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => worker.EnqueueCriticalAsync(Snapshot()));
        }
        finally { repository.ReleaseWrite.TrySetResult(); }
    }

    [TestMethod]
    public async Task DrainCommitsAcceptedWorkBeforeRejectingFurtherPublication()
    {
        var repository = new FixtureRepository(blockFirstWrite: true);
        await using var worker = new DownloadPersistenceWorker(repository);
        var accepted = worker.EnqueueCriticalAsync(Snapshot());
        await repository.WriteStarted.Task.WaitAsync(Budget);
        var deletion = worker.DeleteAsync(Guid.NewGuid());
        var drain = worker.DrainAsync(Budget);
        try
        {
            Assert.IsFalse(worker.IsAccepting);
            Assert.IsFalse(drain.IsCompleted);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => worker.EnqueueCriticalAsync(Snapshot()));
            Assert.ThrowsExactly<ObjectDisposedException>(() => worker.DeleteAsync(Guid.NewGuid()));
            worker.EnqueueProgress(Snapshot());
            repository.ReleaseWrite.TrySetResult();
            await Task.WhenAll(accepted, deletion, drain).WaitAsync(Budget);
            Assert.AreEqual(1, repository.WriteCount, "Rejected progress must not survive behind the drained worker.");
            Assert.AreEqual(1, repository.DeleteCount);
            await worker.DrainAsync(Budget);
        }
        finally { repository.ReleaseWrite.TrySetResult(); }
    }

    [TestMethod]
    public async Task CoalescedProgressPersistsLatestSnapshotWithoutAnotherCriticalWake()
    {
        var repository = new FixtureRepository(blockFirstWrite: true);
        await using var worker = new DownloadPersistenceWorker(repository);
        var initial = Snapshot();
        var accepted = worker.EnqueueCriticalAsync(initial);
        await repository.WriteStarted.Task.WaitAsync(Budget);
        try
        {
            worker.EnqueueProgress(initial with
            {
                DownloadedBytes = 1_048_576,
                UpdatedAt = initial.UpdatedAt.AddSeconds(1),
            });
            var latest = initial with
            {
                DownloadedBytes = 2_097_152,
                UpdatedAt = initial.UpdatedAt.AddSeconds(2),
            };
            worker.EnqueueProgress(latest);
            repository.ReleaseWrite.TrySetResult();

            await Task.WhenAll(accepted, repository.ProgressWritten.Task).WaitAsync(Budget);
            await worker.DrainAsync(Budget);
            Assert.AreEqual(latest, repository.LastWritten);
            Assert.AreEqual(2, repository.WriteCount, "Coalescing should retain the latest progress rather than every report.");
        }
        finally { repository.ReleaseWrite.TrySetResult(); }
    }

    [TestMethod]
    public async Task RacingPublicationAndDrainSettleEveryAcceptedCompletion()
    {
        var repository = new FixtureRepository(blockFirstWrite: false);
        await using var worker = new DownloadPersistenceWorker(repository);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await worker.EnqueueCriticalAsync(Snapshot());
                return true;
            }
            catch (ObjectDisposedException) { return false; }
        })).ToArray();
        var drain = Task.Run(async () =>
        {
            await start.Task;
            await worker.DrainAsync(Budget);
        });
        start.TrySetResult();

        await Task.WhenAll(publishers.Cast<Task>().Append(drain)).WaitAsync(Budget);
        var outcomes = await Task.WhenAll(publishers);
        Assert.AreEqual(outcomes.Count(accepted => accepted), repository.WriteCount,
            "Every accepted completion must correspond to a committed write before drain returns.");
    }

    private static DownloadTaskSnapshot Snapshot()
    {
        var request = new DownloadRequest(Guid.NewGuid(), "https://example.com/file", Path.GetTempPath(), "file.bin");
        return new DownloadTaskSnapshot { Id = request.TaskId, Request = request, RunId = 1 };
    }

    private sealed class FixtureRepository(bool blockFirstWrite) : IDownloadTaskRepository
    {
        private int _writeCount;
        private int _deleteCount;
        public int WriteCount => Volatile.Read(ref _writeCount);
        public int DeleteCount => Volatile.Read(ref _deleteCount);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProgressWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DownloadTaskSnapshot? LastWritten { get; private set; }

        public async Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            var write = Interlocked.Increment(ref _writeCount);
            if (write == 1 && blockFirstWrite)
            {
                WriteStarted.TrySetResult();
                await ReleaseWrite.Task.WaitAsync(Budget);
            }
            LastWritten = snapshot;
            if (write > 1) ProgressWritten.TrySetResult();
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _deleteCount);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadTaskSnapshot>>([]);
    }
}
