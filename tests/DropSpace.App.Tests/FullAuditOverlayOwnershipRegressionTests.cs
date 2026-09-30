using System.Reflection;
using System.Runtime.CompilerServices;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class FullAuditOverlayOwnershipRegressionTests
{
    [TestMethod]
    [DataRow("ActivationHost", false)]
    [DataRow("ActivationHost", true)]
    [DataRow("VisualOverlay", true)]
    [DataRow("SmartDetector", false)]
    public void RetiredNativeTargetCancelsItsDragState(string owner, bool topologyRebuild)
    {
        var fixture = CreateFixture(owner);
        using var gate = fixture.Gate;
        using var ready = fixture.Ready;
        ConfigureDisabled(fixture.Service, topologyRebuild);
        Assert.AreEqual(OverlayState.Compact, fixture.State.Snapshot.State);
        Assert.AreEqual("None", Get(fixture.Service, "_activeDragOwner")!.ToString());
    }

    [TestMethod]
    public void WakeModeChangePreservesStillRegisteredVisibleTarget()
    {
        var fixture = CreateFixture("VisualOverlay");
        using var gate = fixture.Gate;
        using var ready = fixture.Ready;
        ConfigureDisabled(fixture.Service, false);
        Assert.AreEqual(OverlayState.DragReady, fixture.State.Snapshot.State);
        Assert.AreEqual("VisualOverlay", Get(fixture.Service, "_activeDragOwner")!.ToString());
    }

    private static (OverlayWindowService Service, OverlayStateMachine State,
        SemaphoreSlim Gate, ManualResetEventSlim Ready) CreateFixture(string owner)
    {
        // No HWND, WinRT view or global hook is constructed. The collections are
        // empty, matching the rebuild point after the old native targets retire.
        var state = new OverlayStateMachine();
        state.Restore(1);
        state.BeginDragApproach();
        state.SetDragReady(true);
        var view = Uninitialized<OverlayViewModel>();
        Set(view, "_stateMachine", state);
        var detector = Uninitialized<DragSessionDetector>();
        var gate = new SemaphoreSlim(1, 1);
        var ready = new ManualResetEventSlim();
        Set(detector, "_lifecycleSemaphore", gate);
        Set(detector, "_observerRegistrationReady", ready);
        Set(detector, "_mode", FileDragWakeMode.Disabled);
        var service = Uninitialized<OverlayWindowService>();
        Set(service, "_viewModel", view);
        Set(service, "_activationHosts", new List<DragActivationHost>());
        Set(service, "_dragDropService", Uninitialized<OleDragDropService>());
        Set(service, "_dragSessionDetector", detector);
        Set(service, "_configuredWakeMode", FileDragWakeMode.ClassicTopEdge);
        Set(service, "_logger", NullLogger<OverlayWindowService>.Instance);
        var ownerType = typeof(OverlayWindowService).GetNestedType("DragTargetOwner", BindingFlags.NonPublic)!;
        Set(service, "_activeDragOwner", Enum.Parse(ownerType, owner));
        return (service, state, gate, ready);
    }

    private static void ConfigureDisabled(OverlayWindowService service, bool force) =>
        typeof(OverlayWindowService).GetMethod("ConfigureWakeMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [FileDragWakeMode.Disabled, force]);

    private static T Uninitialized<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static object? Get(object target, string field) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
