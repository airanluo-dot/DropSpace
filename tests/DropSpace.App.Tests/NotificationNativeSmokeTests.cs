using DropSpace.App.Services.Notifications;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NotificationNativeSmokeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task ChecksAccessAndDrainsWithoutRequestingPermission()
    {
        await using var service = new WindowsNotificationActivityService(NullLogger<WindowsNotificationActivityService>.Instance);
        Assert.AreEqual(NotificationAccessState.Disabled, service.AccessState);
        await service.SetEnabledAsync(true);
        TestContext.WriteLine("Actual notification access: {0}", service.AccessState);
        Assert.AreNotEqual(NotificationAccessState.Disabled, service.AccessState);
        await service.SetEnabledAsync(false);
        Assert.AreEqual(NotificationAccessState.Disabled, service.AccessState);
        await service.SetEnabledAsync(true);
        await service.SetEnabledAsync(false);
    }
}
