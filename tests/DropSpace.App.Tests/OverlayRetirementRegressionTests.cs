using System.Reflection;
using System.Runtime.CompilerServices;
using DropSpace.App.Services;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class OverlayRetirementRegressionTests
{
    [TestMethod]
    [DataRow("_disposed")]
    [DataRow("_rebuildingSurfaces")]
    public void RetiredSurfaceServiceDoesNotReadOrPublishNativeState(string field)
    {
        var service = (OverlayWindowService)RuntimeHelpers.GetUninitializedObject(typeof(OverlayWindowService));
        typeof(OverlayWindowService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, true);
        // Deliberately no native windows or experience coordinator: retirement must
        // short-circuit before either is touched by a reentrant presentation event.
        typeof(OverlayWindowService).GetMethod("ApplySnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [null]);
    }
}
