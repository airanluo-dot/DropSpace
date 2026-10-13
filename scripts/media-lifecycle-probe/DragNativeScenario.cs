using System.Collections.Concurrent;
using System.Reflection;
using DropSpace.App.Services;
using DropSpace.Core.Models;
using Windows.Foundation;
using Windows.Storage;

internal static class DragNativeScenario
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        var service = new DragStorageItemService(null!); // File references do not use the payload store.
        var gate = (SemaphoreSlim)typeof(DragStorageItemService)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        using var session = new CancellationTokenSource();
        var owners = Enumerable.Range(0, 9).Select(_ => new ItemAssetWorkLifetime(session.Token)).ToArray();
        using var freshOwner = new ItemAssetWorkLifetime(session.Token);
        var calls = new List<Task<IStorageItem?>>();
        StorageFile.Reset();
        try
        {
            for (var index = 0; index < 8; index++)
                calls.Add(service.ResolveAsync(Item($"old-{index}"), owners[index].Token));
            await StorageFile.EightStarted.Task.WaitAsync(Deadline);
            calls.Add(service.ResolveAsync(Item("old-queued"), owners[8].Token));
            Check(StorageFile.Requests.Count == 8 && gate.CurrentCount == 0,
                "Fixture did not fill exactly eight native lookup slots.");

            foreach (var owner in owners) owner.Cancel();
            await Task.WhenAll(calls.Select(ExpectCancelledAsync)).WaitAsync(Deadline);
            foreach (var owner in owners) owner.Dispose();
            await Task.WhenAll(StorageFile.Requests.Values.Select(value => value.CancelRequested.Task)).WaitAsync(Deadline);
            Check(StorageFile.Requests.Count == 8 && !StorageFile.Requests.ContainsKey("old-queued"),
                "A retired queued asset request started native work.");
            Check(gate.CurrentCount == 0 && StorageFile.Requests.Values.All(value => !value.IsCompleted),
                "Caller retirement released capacity before actual native completion.");

            var fresh = service.ResolveAsync(Item("fresh"), freshOwner.Token);
            calls.Add(fresh);
            Check(!fresh.IsCompleted && StorageFile.Requests.Count == 8,
                "Fresh work bypassed slots still owned by old native lookups.");
            foreach (var operation in StorageFile.Requests.Values.ToArray()) operation.Complete();
            await StorageFile.FreshStarted.Task.WaitAsync(Deadline);
            Check(!StorageFile.Requests.ContainsKey("old-queued"),
                "Retired queued work revived after native capacity became available.");
            StorageFile.Requests["fresh"].Complete();
            Check((await fresh.WaitAsync(Deadline) as StorageFile)?.Path == "fresh",
                "Fresh drag lookup did not recover after old native completion.");
            await DrainSlotsAsync(gate);
            Check(gate.CurrentCount == 8 && StorageFile.Requests.Count == 9 && calls.Take(9).All(call => call.IsCanceled),
                "Native retirement leaked slots or revived canceled old callers.");
            Console.WriteLine("PASS drag-native: actual asset owners cancel eight callers and one queued request; old native lookups retain all slots until Completed; queued old work never starts; fresh lookup recovers and all eight slots return.");
        }
        finally
        {
            foreach (var owner in owners) owner.Cancel();
            freshOwner.Cancel();
            foreach (var operation in StorageFile.Requests.Values) operation.Complete();
            foreach (var call in calls)
            {
                try { await call.WaitAsync(Deadline); }
                catch (OperationCanceledException) { }
            }
            await DrainSlotsAsync(gate);
            foreach (var owner in owners) owner.Dispose();
        }
    }

    private static async Task ExpectCancelledAsync(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("A retired asset caller returned a result.");
    }

    private static async Task DrainSlotsAsync(SemaphoreSlim gate)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        var held = 0;
        try
        {
            for (; held < 8; held++) await gate.WaitAsync(deadline.Token);
        }
        finally { if (held > 0) gate.Release(held); }
    }

    private static DropItem Item(string path) => new(Guid.NewGuid(), ItemSource.Space, ItemKind.File,
        path, DateTimeOffset.UtcNow, null, false, ItemStatus.Available, path, 1, null, null,
        new(path, path, FileEntryKind.File, ".txt", 1, null, null, null), null, null, null, null);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace Windows.Storage
{
    // Only GetFileFromPathAsync's platform boundary is controlled. The actual
    // service decides admission, waiting, cancellation and native retirement.
    public interface IStorageItem;
    public sealed class StorageFile(string path) : IStorageItem
    {
        internal string Path { get; } = path;
        internal static ConcurrentDictionary<string, DeferredStorageLookup> Requests { get; } = new();
        internal static TaskCompletionSource EightStarted { get; private set; } = Signal();
        internal static TaskCompletionSource FreshStarted { get; private set; } = Signal();
        internal static void Reset()
        {
            Requests.Clear(); EightStarted = Signal(); FreshStarted = Signal();
        }
        public static IAsyncOperation<StorageFile> GetFileFromPathAsync(string path)
        {
            var operation = new DeferredStorageLookup(path);
            if (!Requests.TryAdd(path, operation)) throw new InvalidOperationException("Duplicate native lookup.");
            if (Requests.Count == 8) EightStarted.TrySetResult();
            if (path == "fresh") FreshStarted.TrySetResult();
            return operation;
        }
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class StorageFolder : IStorageItem
    {
        public static IAsyncOperation<StorageFolder> GetFolderFromPathAsync(string path) =>
            throw new NotSupportedException("Folder native activation is outside this focused file-lookup scenario.");
    }

    internal sealed class DeferredStorageLookup(string path) : IAsyncOperation<StorageFile>
    {
        private readonly TaskCompletionSource<StorageFile> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsCompleted => _completed.Task.IsCompleted;
        // Projected caller cancellation and native Completed are deliberately independent.
        public Task<StorageFile> AsTask(CancellationToken token) => _completed.Task.WaitAsync(token);
        public void Cancel() => CancelRequested.TrySetResult();
        internal void Complete() => _completed.TrySetResult(new StorageFile(path));
    }
}
