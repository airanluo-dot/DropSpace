using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DownloadPersistenceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task DeleteRetiresProgressRequeuedByAnInflightFailedWrite()
    {
        var repository = new BlockedProgressRepository();
        await using var worker = new DownloadPersistenceWorker(repository);
        var request = new DownloadRequest(Guid.NewGuid(), "https://example.com/file", Path.GetTempPath(), "file.bin");
        var snapshot = new DownloadTaskSnapshot { Id = request.TaskId, Request = request, RunId = 1 };
        await worker.EnqueueCriticalAsync(snapshot).WaitAsync(Budget);
        worker.EnqueueProgress(snapshot with
        {
            DownloadedBytes = 1_048_576,
            UpdatedAt = snapshot.UpdatedAt.AddSeconds(1),
        });

        try
        {
            await repository.ProgressStarted.Task.WaitAsync(Budget);
            var deleted = worker.DeleteAsync(snapshot.Id);
            repository.FailProgress.TrySetResult();
            await deleted.WaitAsync(Budget);
            await worker.DrainAsync(Budget);

            Assert.IsNull(repository.Stored, "A failed progress retry must not recreate a deleted history row.");
            Assert.AreEqual(2, repository.WriteCount, "Deletion must retire the failed progress retry.");
            Assert.AreEqual(1, repository.DeleteCount);
        }
        finally
        {
            repository.FailProgress.TrySetResult();
        }
    }

    private sealed class BlockedProgressRepository : IDownloadTaskRepository
    {
        public TaskCompletionSource ProgressStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FailProgress { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DownloadTaskSnapshot? Stored { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }

        public async Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (++WriteCount == 2)
            {
                ProgressStarted.TrySetResult();
                await FailProgress.Task.WaitAsync(Budget, cancellationToken);
                throw new IOException("Fixture progress checkpoint failed during history deletion.");
            }
            Stored = snapshot;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            Stored = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadTaskSnapshot>>(Stored is { } snapshot ? [snapshot] : []);
    }
}
