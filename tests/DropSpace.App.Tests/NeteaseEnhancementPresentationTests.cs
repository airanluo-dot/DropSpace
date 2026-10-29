using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NeteaseEnhancementPresentationTests
{
    [TestMethod]
    public async Task OpeningChecksOnceAndOnlyManualChecksReportNetworkFailure()
    {
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            var service = new DeferredEnhancement();
            service.Publish(new(NeteaseEnhancementStage.Installed, true, "3.2.11"));
            var calls = 0;
            var installed = "3.4.0";
            using var view = new NeteaseEnhancementViewModel(service, IdentityAppStringLocalizer.Instance,
                controller.DispatcherQueue, () => installed, _ =>
                {
                    calls++;
                    return calls == 1 ? Task.FromResult<string?>("3.5.0")
                        : Task.FromException<string?>(new HttpRequestException());
                });
            Assert.AreEqual(0, calls, "Creating the background window must not check updates.");
            await view.CheckOnFirstOpenAsync();
            await view.CheckOnFirstOpenAsync();
            Assert.AreEqual(1, calls);
            Assert.IsTrue(view.HasUpdate, "Compare actual package version, not receipt history.");
            installed = "3.5.0";
            await view.CheckUpdateAsync(silent: true);
            Assert.IsFalse(view.HasUpdate);
            Assert.AreEqual(string.Empty, view.UpdateStatus);
            await view.CheckUpdateAsync();
            Assert.AreEqual("NeteaseUpdateUnavailable", view.UpdateStatus);
            Assert.IsFalse(service.Completion.Task.IsCompleted, "Checking does not install anything.");
        }
        finally { await controller.ShutdownQueueAsync(); }
    }

    [TestMethod]
    public async Task CancellationRemainsBusyUntilInstallerRecoveryCompletes()
    {
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            var service = new DeferredEnhancement();
            using var view = new NeteaseEnhancementViewModel(service, IdentityAppStringLocalizer.Instance, controller.DispatcherQueue);
            var operation = view.EnhanceAsync();
            Assert.IsTrue(view.CanCancel);
            view.Cancel();
            Assert.IsTrue(service.Token.IsCancellationRequested);
            Assert.IsTrue(view.IsBusy);
            Assert.IsFalse(view.CanCancel);
            Assert.AreEqual("DlcCanceling", view.Status);
            Assert.IsFalse(operation.IsCompleted, "Cancellation must await recovery before actions become available.");
            service.Publish(new(NeteaseEnhancementStage.Failed, ErrorCode: "Canceled"));
            service.Completion.SetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(view.IsBusy);
            Assert.IsFalse(view.IsCancelableOperation);
            Assert.AreEqual("NeteaseEnhancementErrorCanceled", view.Error);
        }
        finally { await controller.ShutdownQueueAsync(); }
    }

    [TestMethod]
    public async Task BusyIsImmediateAndQueuedStagesCannotRestoreAnOldInstallationState()
    {
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.DispatcherQueue.TryEnqueue(() =>
        {
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var service = new DeferredEnhancement();
            using var view = new NeteaseEnhancementViewModel(service, IdentityAppStringLocalizer.Instance, controller.DispatcherQueue);
            var operation = view.EnhanceAsync(reinstall: true);
            Assert.IsTrue(view.IsBusy, "Both entry points must disable actions before a worker publishes its first stage.");
            service.Publish(new(NeteaseEnhancementStage.Preparing));
            service.Publish(new(NeteaseEnhancementStage.Verifying, true));
            service.Publish(new(NeteaseEnhancementStage.Failed, true, ErrorCode: "Verification"));
            service.Completion.SetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(view.IsBusy);
            Assert.AreEqual("NeteaseEnhancementErrorVerification", view.Error);
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.DispatcherQueue.TryEnqueue(() => drained.SetResult());
            release.Set();
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(view.IsBusy);
            Assert.IsTrue(view.IsManaged);
            Assert.IsFalse(view.IsEnhanced);
            Assert.AreEqual("NeteaseEnhancementFailed", view.Status);
        }
        finally
        {
            release.Set();
            await controller.ShutdownQueueAsync();
        }
    }

    private sealed class DeferredEnhancement : INeteaseEnhancementService
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public NeteaseEnhancementState Current { get; private set; } = new(NeteaseEnhancementStage.NotInstalled);
        public event EventHandler<NeteaseEnhancementState>? Changed;
        public void Publish(NeteaseEnhancementState state) { Current = state; Changed?.Invoke(this, state); }
        public Task InspectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnhanceAsync(bool reinstall = false, CancellationToken cancellationToken = default)
        { Token = cancellationToken; return Completion.Task; }
        public Task RemoveAsync(CancellationToken cancellationToken = default) => Completion.Task;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
