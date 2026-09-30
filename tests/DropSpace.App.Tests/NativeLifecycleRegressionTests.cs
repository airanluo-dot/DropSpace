using System.Reflection;
using System.Runtime.InteropServices;
using DropSpace.App.Services;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NativeLifecycleRegressionTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    public void CompatibilityErrorDialogUsesAnExistingUnicodeExport()
    {
        var method = typeof(WindowsCompatibilityErrorDialog).GetMethod(
            "MessageBox", BindingFlags.Static | BindingFlags.NonPublic)!;
        var binding = method.GetCustomAttribute<DllImportAttribute>()!;
        Assert.AreEqual("MessageBoxW", binding.EntryPoint);
        var library = NativeLibrary.Load(binding.Value);
        try
        {
            Assert.IsTrue(NativeLibrary.TryGetExport(library, binding.EntryPoint!, out _));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task DisabledActivationHostHidesAfterOwnedDragLeaves()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(OleInitialize(0));
                initialized = true;
                var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N")));
                var classifier = new OleFileDataClassifier();
                var leases = new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance);
                var materializer = new VirtualFileMaterializer(paths, classifier, NullLogger<VirtualFileMaterializer>.Instance, leases);
                using var host = new DragActivationHost(
                    new MonitorDescriptor("test", 0, 0, 0, 1920, 1080, 96, true),
                    new DragActivationCallbacks(_ => { }, (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask),
                    classifier, materializer, NullLogger<DragActivationHost>.Instance);
                typeof(DragActivationHost).GetMethod("ExpandForDrag", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(host, null);
                host.SetEnabled(false);
                Assert.IsTrue(IsWindowVisible(host.WindowHandle), "An active OLE owner stays visible until the drag finishes.");
                var target = (IOleDropTarget)typeof(DragActivationHost)
                    .GetField("_dropTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                target.DragLeave();
                Assert.IsFalse(IsWindowVisible(host.WindowHandle), "The deferred disable must hide the native input band after DragLeave.");
                completed.TrySetResult();
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
            finally
            {
                if (initialized) OleUninitialize();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
}
