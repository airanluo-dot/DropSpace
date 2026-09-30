using System.Reflection;
using System.Runtime.CompilerServices;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ShellAcknowledgementLifecycleRegressionTests
{
    [TestMethod]
    public async Task CallerCancellationClearsOwnedSourceAndAllowsAnotherAcknowledgement()
    {
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            var view = CreateView(controller.DispatcherQueue);
            for (var index = 1; index <= 2; index++)
            {
                using var stop = new CancellationTokenSource();
                var installed = ObserveInstallation(view);
                var acknowledgement = view.ShowShellIntakeAcknowledgementAsync(index, stop.Token);
                await installed.WaitAsync(TimeSpan.FromSeconds(5));
                stop.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => acknowledgement);
                Assert.IsNull(Get(view, "_shellAcknowledgementCancellation"));
                Assert.IsNull(Get(view, "_shellAcknowledgement"));
            }
        }
        finally { await controller.ShutdownQueueAsync(); }
    }

    [TestMethod]
    public async Task ReplacedAcknowledgementCannotClearLatestMessage()
    {
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            var view = CreateView(controller.DispatcherQueue);
            var firstInstalled = ObserveInstallation(view);
            var first = view.ShowShellIntakeAcknowledgementAsync(1);
            await firstInstalled.WaitAsync(TimeSpan.FromSeconds(5));
            using var stop = new CancellationTokenSource();
            var secondInstalled = ObserveInstallation(view);
            var second = view.ShowShellIntakeAcknowledgementAsync(2, stop.Token);
            await secondInstalled.WaitAsync(TimeSpan.FromSeconds(5));
            var latestOwner = Get(view, "_shellAcknowledgementCancellation");
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(latestOwner, Get(view, "_shellAcknowledgementCancellation"));
            Assert.AreEqual("ShellIntakeAddedCount", Get(view, "_shellAcknowledgement"));
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        }
        finally { await controller.ShutdownQueueAsync(); }
    }

    [TestMethod]
    public async Task QueuedAcknowledgementRejectsRetiredViewBeforePublishing()
    {
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            var view = CreateView(controller.DispatcherQueue);
            Set(view, "_disposed", true);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => view.ShowShellIntakeAcknowledgementAsync(1));
            Assert.IsNull(Get(view, "_shellAcknowledgementCancellation"));
            Assert.IsNull(Get(view, "_shellAcknowledgement"));
        }
        finally { await controller.ShutdownQueueAsync(); }
    }

    private static Task ObserveInstallation(OverlayViewModel view)
    {
        var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler? observer = null;
        observer = (_, args) =>
        {
            if (args.PropertyName != nameof(OverlayViewModel.CompactTitle) ||
                Get(view, "_shellAcknowledgementCancellation") is null) return;
            view.PropertyChanged -= observer;
            installed.TrySetResult();
        };
        view.PropertyChanged += observer;
        return installed.Task;
    }

    private static OverlayViewModel CreateView(DispatcherQueue dispatcher)
    {
        // The acknowledgement owns only localization and dispatcher state: no HWND,
        // projection coordinator, main window or global hook is constructed.
        var view = (OverlayViewModel)RuntimeHelpers.GetUninitializedObject(typeof(OverlayViewModel));
        Set(view, "_dispatcher", dispatcher);
        Set(view, "_strings", IdentityAppStringLocalizer.Instance);
        return view;
    }

    private static object? Get(object owner, string field) => owner.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void Set(object owner, string field, object value) => owner.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
}
