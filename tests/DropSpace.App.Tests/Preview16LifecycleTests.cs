using DropSpace.App.Services;
using DropSpace.Infrastructure.Actions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.Versioning;

namespace DropSpace.App.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class Preview16LifecycleTests
{
    [TestMethod]
    [DataRow("Ctrl+Ctrl+A")]
    [DataRow("Ctrl+A+B")]
    [DataRow("Ctrl++A")]
    [DataRow("+Ctrl+A")]
    [DataRow("Ctrl+A+")]
    [DataRow("A")]
    [DataRow("Ctrl")]
    [DataRow("Ctrl+Escape")]
    public void InvalidHotkeysAreRejected(string gesture) =>
        Assert.ThrowsExactly<ArgumentException>(() => GlobalQuickPanelHotkeyService.Parse(gesture));

    [TestMethod]
    public void ValidHotkeyHasExactlyOneKey()
    {
        var key = GlobalQuickPanelHotkeyService.Parse("Win+Shift+Space");
        Assert.AreEqual(0x20u, key.VirtualKey);
        Assert.AreEqual(12u, key.Modifiers);
    }

    [TestMethod]
    public void ThrowingNativeSubscribersDoNotPreventLaterNotifications()
    {
        var calls = 0;
        EventHandler<int> handlers = (_, _) => throw new InvalidOperationException();
        handlers += (_, value) => calls += value;
        for (var i = 0; i < 2; i++) NativeSubscriberNotification.Invoke(handlers, this, 1, NullLogger.Instance);
        Assert.AreEqual(2, calls);
        EventHandler hotkey = (_, _) => throw new InvalidOperationException();
        hotkey += (_, _) => calls++;
        NativeSubscriberNotification.Invoke(hotkey, this, NullLogger.Instance);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task EveryRollbackRunsInReverseOrderDespiteFailures()
    {
        for (var failed = 0; failed < 5; failed++)
        {
            var coordinator = new SettingsTransactionRollbackCoordinator();
            var calls = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                var index = i;
                coordinator.Committed("test", () =>
                {
                    calls.Add(index);
                    return index == failed ? Task.FromException(new IOException()) : Task.CompletedTask;
                });
            }
            await coordinator.RollbackAsync((_, _) => throw new InvalidOperationException());
            CollectionAssert.AreEqual(new[] { 4, 3, 2, 1, 0 }, calls);
        }
    }

    [TestMethod]
    public void QrCapacityUsesUtf8AndExecutionEnforcesIt()
    {
        Assert.IsTrue(QrCodeActionService.CanEncode("https://example.invalid/"));
        Assert.IsFalse(QrCodeActionService.CanEncode(new string('界', 600)));
        Assert.ThrowsExactly<InvalidDataException>(() => QrCodeActionService.RenderPng(new string('a', 2000)));
        Assert.IsTrue(QrCodeActionService.RenderPng(new string('a', 1600)).Length > 0);
    }
}
