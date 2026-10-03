using System.Reflection;
using System.Runtime.CompilerServices;
using DropSpace.App.ViewModels;
using DropSpace.Core.Actions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class QuickActionCancellationTests
{
    [TestMethod]
    public async Task ShutdownDoesNotDisposeTheSemaphoreWhileADialogOwnsIt()
    {
        var service = new DropSpace.App.Services.QuickActionDialogService(null!, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DropSpace.App.Services.QuickActionDialogService>.Instance);
        var gate = (SemaphoreSlim)typeof(DropSpace.App.Services.QuickActionDialogService)
            .GetField("_dialogGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        await gate.WaitAsync();
        service.Dispose();
        gate.Release();
        await service.DisposeAsync();
    }

    [TestMethod]
    [DataRow("context")]
    [DataRow("caller")]
    [DataRow("view-model")]
    public async Task EveryOwnerCancellationStopsBeforeActionRegistryAccess(string owner)
    {
        using var contextStop = new CancellationTokenSource();
        using var callerStop = new CancellationTokenSource();
        using var viewStop = new CancellationTokenSource();
        if (owner == "context") contextStop.Cancel();
        if (owner == "caller") callerStop.Cancel();
        if (owner == "view-model") viewStop.Cancel();
        var view = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel).GetField("_lifetimeCancellation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, viewStop);
        var card = (ItemCardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ItemCardViewModel));
        var selection = new ItemSelectionSnapshot([]);
        var context = new ItemActionContext(selection, CancellationToken: contextStop.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            view.ExecuteQuickActionAsync(card, ItemActionId.ConvertImage, context, selection, callerStop.Token));
    }
}
