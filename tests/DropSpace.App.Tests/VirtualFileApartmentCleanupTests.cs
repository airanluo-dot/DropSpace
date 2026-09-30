using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;
using DropSpace.App.Services;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class VirtualFileApartmentCleanupTests
{
    [TestMethod]
    public async Task AsynchronousRollbackEndsTheOleOperationOnItsSupplyingApartment()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await RunOnOwnedThreadAsync(async () =>
            {
                var paths = new AppStoragePaths(root);
                var leases = new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance);
                // Format query rejects before native descriptor access, so no native allocation is needed.
                var classifier = (OleFileDataClassifier)RuntimeHelpers.GetUninitializedObject(typeof(OleFileDataClassifier));
                var source = new RejectingAsyncSource(leases);
                var materializer = new VirtualFileMaterializer(paths, classifier, NullLogger<VirtualFileMaterializer>.Instance, leases);
                var owner = Environment.CurrentManagedThreadId;
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => materializer.MaterializeAsync(source));
                Assert.AreEqual(owner, source.EndThread, "Rollback must preserve the supplying OLE apartment through EndOperation.");
                Assert.AreEqual(1, source.EndCount);
                Assert.IsTrue(source.EndResult < 0);
                Assert.AreEqual(0, Directory.GetFiles(paths.StagingLeases, "*.json").Length);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Task RunOnOwnedThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new OwnedContext();
            SynchronizationContext.SetSynchronizationContext(context);
            async void Execute()
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { context.Complete(); }
            }
            Execute();
            context.Run();
        }) { IsBackground = true };
        thread.Start();
        return completion.Task;
    }

    private sealed class OwnedContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public void Run() { foreach (var work in _queue.GetConsumingEnumerable()) work.Callback(work.State); }
        public void Complete() => _queue.CompleteAdding();
        public void Dispose() => _queue.Dispose();
    }

    private sealed class RejectingAsyncSource(StagingLeaseStore leases) : IDataObject, VirtualFileMaterializer.IDataObjectAsyncCapability
    {
        public int EndThread { get; private set; }
        public int EndCount { get; private set; }
        public int EndResult { get; private set; }
        public int QueryGetData(ref FORMATETC format)
        {
            // Force real CompleteAsync to await its gate rather than completing synchronously.
            var gate = (SemaphoreSlim)typeof(StagingLeaseStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(leases)!;
            gate.Wait();
            // The owner cannot process this release until MaterializeAsync yields at
            // its rollback await, so the continuation is asynchronous without a timer.
            SynchronizationContext.Current!.Post(_ => gate.Release(), null);
            return 1;
        }
        public void GetData(ref FORMATETC format, out STGMEDIUM medium) => throw new NotSupportedException();
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = default; return 1; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection) { connection = 0; return 1; }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA advise) { advise = null!; return 1; }
        public int SetAsyncMode(bool mode) => 0;
        public int GetAsyncMode(out bool mode) { mode = true; return 0; }
        public int StartOperation(nint context) => 0;
        public int InOperation(out bool active) { active = true; return 0; }
        public int EndOperation(int result, nint context, uint effects)
        { EndThread = Environment.CurrentManagedThreadId; EndCount++; EndResult = result; return 0; }
    }
}
