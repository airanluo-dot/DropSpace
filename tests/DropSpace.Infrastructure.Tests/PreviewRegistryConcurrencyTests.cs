using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Preview;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PreviewRegistryConcurrencyTests
{
    [TestMethod]
    public async Task BlockingProviderReturnsControlAndCancelledCallersRetainWorkerCapacity()
    {
        using var provider = new BlockingProvider();
        var registry = new PreviewProviderRegistry([provider], new EmptyCache(), NullLogger<PreviewProviderRegistry>.Instance);
        using var firstCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var request = new PreviewRequest(new(Guid.NewGuid(), ItemKind.File, ItemStatus.Available,
            "test", null, ".txt", null, null, null, null, 1));
        Task<PreviewDescriptor>? first = null;
        Task<PreviewDescriptor>? second = null;
        Task<PreviewCapability>? queued = null;
        var firstInvocation = Task.Factory.StartNew(() => registry.LoadAsync(request, firstCancellation.Token),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var secondInvocation = Task.Factory.StartNew(() => registry.LoadAsync(request),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            first = await firstInvocation.WaitAsync(TimeSpan.FromSeconds(5));
            second = await secondInvocation.WaitAsync(TimeSpan.FromSeconds(5));
            await provider.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, provider.Calls);

            queued = registry.ProbeAsync(request.Item, queuedCancellation.Token);
            firstCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(2, provider.Calls, "A cancelled caller still owns capacity while its provider is blocked.");
            Assert.IsFalse(queued.IsCompleted, "The queued probe must wait for actual worker completion.");

            queuedCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(2, provider.Calls);
            provider.Release.Set();
            Assert.AreEqual(PreviewKind.Text, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Kind);
            Assert.IsTrue((await registry.ProbeAsync(request.Item).WaitAsync(TimeSpan.FromSeconds(5))).CanPreview);
        }
        finally
        {
            provider.Release.Set();
            firstCancellation.Cancel();
            queuedCancellation.Cancel();
            await ObserveAsync(await firstInvocation);
            await ObserveAsync(await secondInvocation);
            if (queued is not null) await ObserveAsync(queued);
            await provider.InitialWorkFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
    }

    private sealed class BlockingProvider : IPreviewProvider, IDisposable
    {
        private int _calls;
        private int _finished;
        public int Calls => Volatile.Read(ref _calls);
        public ManualResetEventSlim Release { get; } = new(false);
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InitialWorkFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "blocking";
        public int Priority => 100;
        public ValueTask<PreviewCapability> ProbeAsync(DropItemSnapshot item, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 2) BothStarted.TrySetResult();
            try
            {
                Release.Wait();
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new PreviewCapability(true, PreviewKind.Text, Id, "text/plain", null, null, null, null, null));
            }
            finally
            {
                if (Interlocked.Increment(ref _finished) == 2) InitialWorkFinished.TrySetResult();
            }
        }
        public Task<PreviewDescriptor> LoadAsync(PreviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PreviewDescriptor(request.Item.Id, PreviewKind.Text, request.Item.Title,
                "text/plain", "text", null, null, null, null, null, new Dictionary<string, string>()));
        public void Dispose() => Release.Dispose();
    }

    private sealed class EmptyCache : IPreviewCache
    {
        public long Generation => 0;
        public Task<PreviewDescriptor?> TryGetAsync(Guid itemId, int revision, PreviewKind kind, int page, int targetPixelWidth, CancellationToken cancellationToken = default) => Task.FromResult<PreviewDescriptor?>(null);
        public Task PutAsync(PreviewRequest request, PreviewDescriptor descriptor, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
