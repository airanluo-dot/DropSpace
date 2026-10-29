using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NeteaseEnhancementPresentationTests
{
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
