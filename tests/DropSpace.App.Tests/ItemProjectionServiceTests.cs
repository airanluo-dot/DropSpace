using System.Reflection;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ItemProjectionServiceTests
{
    [TestMethod]
    public async Task LoadPageAsync_ReturnsToCallerWhileSynchronousRepositoryIsBlocked()
    {
        using var release = new ManualResetEventSlim();
        var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new ItemQueryPage([], null, false);
        var service = CreateService((_, _, token) =>
        {
            entered.TrySetResult();
            release.Wait(token);
            return Task.FromResult(expected);
        });
        var caller = Task.Run(async () =>
        {
            var pending = service.LoadPageAsync(new("Space", ""));
            returned.TrySetResult(pending.IsCompleted);
            return await pending;
        });

        try
        {
            Assert.IsFalse(await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            Assert.AreSame(expected, await caller.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task LoadPageAsync_PreservesQueryCursorTokenAndPage()
    {
        var cursor = new ItemQueryCursor(2, DateTimeOffset.UtcNow, Guid.NewGuid());
        var expected = new ItemQueryPage([], cursor, true);
        using var cancellation = new CancellationTokenSource();
        var service = CreateService((query, receivedCursor, token) =>
        {
            Assert.AreEqual("find me", query.Search);
            Assert.AreEqual(ItemProjectionService.DefaultPageSize, query.Limit);
            Assert.AreEqual(0, query.Offset);
            Assert.IsNull(query.Source);
            Assert.AreSame(cursor, receivedCursor);
            Assert.AreEqual(cancellation.Token, token);
            return Task.FromResult(expected);
        });

        Assert.AreSame(expected, await service.LoadPageAsync(new("Pinned", "find me"), cursor, cancellation.Token));
    }

    [TestMethod]
    public async Task LoadPageAsync_PreCanceledRequestDoesNotStartRepositoryWork()
    {
        var calls = 0;
        var service = CreateService((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ItemQueryPage([], null, false));
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await service.LoadPageAsync(new("Space", ""), cancellationToken: cancellation.Token));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task LoadPageAsync_CancellationReachesSynchronousRepositoryOwner()
    {
        using var neverReleased = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var service = CreateService((_, _, token) =>
        {
            entered.TrySetResult();
            neverReleased.Wait(token);
            return Task.FromResult(new ItemQueryPage([], null, false));
        });
        var pending = service.LoadPageAsync(new("Space", ""), cancellationToken: cancellation.Token);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task LoadPageAsync_PropagatesRepositoryFailure()
    {
        var expected = new IOException("database unavailable");
        var service = CreateService((_, _, _) => throw expected);

        var actual = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await service.LoadPageAsync(new("Space", "")));
        Assert.AreSame(expected, actual);
    }

    private static ItemProjectionService CreateService(
        Func<ItemQuery, ItemQueryCursor?, CancellationToken, Task<ItemQueryPage>> load)
    {
        var repository = DispatchProxy.Create<IItemRepository, PageRepository>();
        ((PageRepository)repository).Load = load;
        return new ItemProjectionService(repository);
    }

    public class PageRepository : DispatchProxy
    {
        public Func<ItemQuery, ItemQueryCursor?, CancellationToken, Task<ItemQueryPage>> Load { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IItemRepository.QueryPageAsync)
                ? Load((ItemQuery)args![0]!, (ItemQueryCursor?)args[1], (CancellationToken)args[2]!)
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
