using System.Reflection;
using System.Runtime.CompilerServices;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class TrayResourceBoundaryTests
{
    [TestMethod]
    public void ExplorerRestartResourceFailureDoesNotEscapeTheNativeCallback()
    {
        var tray = (NativeTrayService)RuntimeHelpers.GetUninitializedObject(typeof(NativeTrayService));
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(NativeTrayService).GetField("_logger", fields)!.SetValue(tray, NullLogger<NativeTrayService>.Instance);
        typeof(NativeTrayService).GetField("_strings", fields)!.SetValue(tray, new UnavailableStrings());
        typeof(NativeTrayService).GetField("_taskbarCreatedMessage", fields)!.SetValue(tray, 0xC123u);
        var callback = typeof(NativeTrayService).GetMethod("WindowSubclassProc", fields)!;
        var result = callback.Invoke(tray, [nint.Zero, 0xC123u, nint.Zero, nint.Zero, UIntPtr.Zero, UIntPtr.Zero]);
        Assert.AreEqual(nint.Zero, result);
        Assert.IsFalse(tray.IsAvailable);
    }

    private sealed class UnavailableStrings : IAppStringLocalizer
    {
        public System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.InvariantCulture;
        public string Get(string key) => throw new InvalidOperationException("The resource broker is unavailable.");
        public bool TryGet(string key, out string value) { value = ""; return false; }
        public string Format(string key, params object?[] arguments) => Get(key);
    }
}
