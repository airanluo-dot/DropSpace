using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DownloadManagerRecoveryTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task CancelledJournalReadAllowsWaitingRecoveryToRetry()
    {
        await using var fixture = new RecoveryFixture();
        fixture.Repository.BlockFirstRead = true;
        using var cancellation = new CancellationTokenSource();
        var cancelled = fixture.Manager.RestoreAsync(cancellation.Token);
        await fixture.Repository.FirstReadStarted.Task.WaitAsync(Budget);
        var retry = fixture.Manager.RestoreAsync();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.WaitAsync(Budget));
        await retry.WaitAsync(Budget);

        Assert.AreEqual(2, fixture.Repository.ReadCount);
        AssertRecovered(fixture);
        await fixture.Manager.RestoreAsync().WaitAsync(Budget);
        Assert.AreEqual(2, fixture.Repository.ReadCount, "A completed recovery must remain idempotent.");
    }

    [TestMethod]
    public async Task CancelledPartialRecoveryPreservesSettledRowsAndRecoversRemainingRows()
    {
        await using var fixture = new RecoveryFixture();
        using var cancellation = new CancellationTokenSource();
        var firstId = fixture.Repository.Snapshots[0].Id;
        EventHandler<DownloadTaskSnapshot> observer = (_, snapshot) =>
        {
            if (snapshot.Id == firstId && snapshot.State == DownloadTaskState.Paused)
                cancellation.Cancel();
        };
        fixture.Manager.TaskChanged += observer;
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => fixture.Manager.RestoreAsync(cancellation.Token).WaitAsync(Budget));
        }
        finally
        {
            fixture.Manager.TaskChanged -= observer;
        }
        var settled = fixture.Manager.Tasks.Single();
        await Task.WhenAll(fixture.Manager.RestoreAsync(), fixture.Manager.RestoreAsync()).WaitAsync(Budget);

        Assert.AreEqual(2, fixture.Repository.ReadCount);
        Assert.AreSame(settled, fixture.Manager.Tasks.Single(item => item.Id == firstId),
            "Retry must not replace the already settled recovery owner.");
        AssertRecovered(fixture);
        Assert.HasCount(2, fixture.Repository.Writes, "Each recovered row needs only one paused checkpoint.");
    }

    [TestMethod]
    public async Task PartialJournalReadPreservesRecoveredRowsAndRetriesUnavailableRows()
    {
        await using var fixture = new RecoveryFixture();
        fixture.Repository.PartialFirstRead = true;
        await fixture.Manager.RestoreAsync().WaitAsync(Budget);
        Assert.AreEqual(nameof(IOException), fixture.Manager.RecoveryError);
        var recovered = fixture.Manager.Tasks.Single();

        await fixture.Manager.RestoreAsync().WaitAsync(Budget);

        Assert.AreEqual(2, fixture.Repository.ReadCount);
        Assert.IsNull(fixture.Manager.RecoveryError);
        Assert.AreSame(recovered, fixture.Manager.Tasks.Single(item => item.Id == recovered.Id));
        AssertRecovered(fixture);
        Assert.HasCount(2, fixture.Repository.Writes, "A partial read retry must checkpoint only newly recovered rows.");
        await fixture.Manager.RestoreAsync().WaitAsync(Budget);
        Assert.AreEqual(2, fixture.Repository.ReadCount, "A successful retry completes recovery.");
    }

    [TestMethod]
    public async Task FailedJournalReadCanRecoverWithoutRestartingManager()
    {
        await using var fixture = new RecoveryFixture();
        fixture.Repository.FailFirstRead = true;
        await fixture.Manager.RestoreAsync().WaitAsync(Budget);
        Assert.AreEqual(nameof(IOException), fixture.Manager.RecoveryError);
        Assert.IsEmpty(fixture.Manager.Tasks);

        await fixture.Manager.RestoreAsync().WaitAsync(Budget);

        Assert.AreEqual(2, fixture.Repository.ReadCount);
        Assert.IsNull(fixture.Manager.RecoveryError);
        AssertRecovered(fixture);
    }

    private static void AssertRecovered(RecoveryFixture fixture)
    {
        Assert.HasCount(2, fixture.Manager.Tasks);
        Assert.IsTrue(fixture.Manager.Tasks.All(item => item.State == DownloadTaskState.Paused));
        Assert.IsTrue(fixture.Manager.Tasks.All(item => item.ActiveConnections == 0 && item.BytesPerSecond == 0));
        Assert.AreEqual(0, fixture.Engine.Transfers.Active, "Recovery must not start transfers.");
        Assert.AreEqual(0, fixture.Engine.Connections.Active, "Recovery must not start HTTP requests.");
    }

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-download-recovery-" + Guid.NewGuid().ToString("N"));
        public HttpRangeDownloader Engine { get; } = new();
        public RecoveryRepository Repository { get; }
        public DownloadManager Manager { get; }

        public RecoveryFixture()
        {
            Directory.CreateDirectory(_root);
            Repository = new(Enumerable.Range(0, 2).Select(index =>
            {
                var request = DirectFileRequestFactory.Create("http://127.0.0.1:1/not-requested", _root, $"recovered-{index}.bin");
                return new DownloadTaskSnapshot
                {
                    Id = request.TaskId, Request = request, OutputPath = Path.Combine(_root, request.OutputFileName),
                    State = DownloadTaskState.DownloadingFile, ActiveConnections = 2, BytesPerSecond = 42,
                };
            }).ToArray());
            Manager = new(Engine, Repository);
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            Engine.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private sealed class RecoveryRepository(IReadOnlyList<DownloadTaskSnapshot> snapshots) : IDownloadTaskRepository
    {
        public IReadOnlyList<DownloadTaskSnapshot> Snapshots { get; } = snapshots;
        public List<DownloadTaskSnapshot> Writes { get; } = [];
        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockFirstRead { get; set; }
        public bool FailFirstRead { get; set; }
        public bool PartialFirstRead { get; set; }
        public string? RecoveryError { get; private set; }
        public int ReadCount { get; private set; }

        public async Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            RecoveryError = null;
            if (++ReadCount == 1)
            {
                FirstReadStarted.TrySetResult();
                if (FailFirstRead) throw new IOException("Fixture journal is temporarily unavailable.");
                if (BlockFirstRead) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (PartialFirstRead)
                {
                    RecoveryError = nameof(IOException);
                    return [Snapshots[0]];
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Snapshots;
        }

        public Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Writes.Add(snapshot);
            return Task.CompletedTask;
        }
    }
}
