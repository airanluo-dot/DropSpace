using DropSpace.App.Services.Widgets;
using DropSpace.Core.Widgets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class WidgetDataNativeSmokeTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task VisibleWidgetsReadRealSystemDataAndHiddenWidgetsStop()
    {
        await using var service = new NativeWidgetDataService(NullLogger<NativeWidgetDataService>.Instance);
        var sampled = new TaskCompletionSource<WidgetDataSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, snapshot) => { if (snapshot.CpuPercent is not null) sampled.TrySetResult(snapshot); };
        Assert.IsNull(service.Current);
        await service.SetVisibleAsync(true);
        var snapshot = await sampled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(snapshot.CpuPercent is >= 0 and <= 100);
        Assert.IsTrue(snapshot.MemoryPercent is > 0 and <= 100);
        Assert.IsTrue((DateTimeOffset.Now - snapshot.LocalTime).Duration() < TimeSpan.FromSeconds(2));
        await service.SetVisibleAsync(false);
        Assert.IsNull(service.Current);
    }
}
