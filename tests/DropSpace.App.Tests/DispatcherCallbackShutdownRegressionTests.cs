using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class DispatcherCallbackShutdownRegressionTests
{
    [TestMethod]
    [DataRow("ApplyCapturedItem")]
    [DataRow("ApplyClipboardStatus")]
    [DataRow("ApplyUpdateStatus")]
    [DataRow("ApplyUndoState")]
    public void RetiredDispatcherCallbackCannotMutateProjectionOrReadDisposedCancellation(string method)
    {
        // Exercise the exact apply boundary used by dispatcher callbacks, after the
        // lifecycle state reached by DisposeAsync. No native queue is needed here.
        var view = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        using var lifetime = new CancellationTokenSource();
        Set(view, "_lifetimeCancellation", lifetime);
        Set(view, "_disposed", true);
        Set(view, "_currentSection", "Clipboard");
        Set(view, "_searchText", string.Empty);
        Set(view, "_strings", IdentityAppStringLocalizer.Instance);
        Set(view, "<Items>k__BackingField", new ObservableCollection<ItemCardViewModel>());
        lifetime.Dispose();
        var notifications = 0;
        view.PropertyChanged += (_, _) => notifications++;
        object argument = method switch
        {
            "ApplyCapturedItem" => new DropItem(Guid.NewGuid(), ItemSource.Clipboard, ItemKind.Text,
                "late capture", DateTimeOffset.UtcNow, null, false, ItemStatus.Available,
                string.Empty, 1, null, null, null, null, null, null, null),
            "ApplyClipboardStatus" => new ClipboardCaptureStatus(ClipboardRecordingState.Recording,
                true, null, 1, 1, 0, 0, 0, "late status"),
            "ApplyUpdateStatus" => UpdateStatusSnapshot.Initial(DeploymentMode.Installer),
            _ => true,
        };

        typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [argument]);

        Assert.AreEqual(0, view.Items.Count);
        Assert.AreEqual(0, notifications, "Retired pages must not receive new bindings or launch new refresh work.");
    }

    [TestMethod]
    public void LiveUpdateCallbackStillPublishesNewStatus()
    {
        var view = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var status = new UpdateStatusSnapshot(UpdateState.Checking, "checking", DeploymentMode.Installer);
        typeof(MainViewModel).GetMethod("ApplyUpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [status]);
        Assert.AreSame(status, view.UpdateStatus);
    }

    [TestMethod]
    [DataRow(250)]
    [DataRow(500)]
    public void LiveClipboardEvictionRewindsPagingToRetainedTail(int loadedCount)
    {
        var view = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var items = new ObservableCollection<ItemCardViewModel>();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index <= loadedCount; index++)
            items.Add(new ItemCardViewModel(new DropItem(Guid.NewGuid(), ItemSource.Clipboard, ItemKind.Text,
                $"row {index}", now.AddSeconds(-index), null, false, ItemStatus.Available,
                string.Empty, 1, null, null, null, null, null, null, null), IdentityAppStringLocalizer.Instance));
        Set(view, "<Items>k__BackingField", items);
        var evicted = items[^1].Id;
        typeof(MainViewModel).GetMethod("TrimLiveClipboardProjection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [loadedCount]);
        Assert.AreEqual(loadedCount, items.Count);
        Assert.IsTrue(view.HasMoreItems);
        var cursor = (ItemQueryCursor)typeof(MainViewModel).GetField("_projectionCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Assert.AreEqual(items[^1].Id, cursor.Id);
        Assert.AreNotEqual(evicted, cursor.Id);
        Assert.AreEqual(items[^1].Item.CreatedAtUtc, cursor.CreatedAtUtc);
    }

    private static void Set(object owner, string field, object value) => owner.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
}
