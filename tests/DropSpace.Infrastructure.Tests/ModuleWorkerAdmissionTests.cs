using DropSpace.Core.Dlc;
using DropSpace.Infrastructure.Dlc;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ModuleWorkerAdmissionTests
{
    [TestMethod]
    public async Task PrecancelledStartRejectsBeforeNativeOrProcessAdmission()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var directory = Path.Combine(Path.GetTempPath(), "DropSpace-cancelled-module-" + Guid.NewGuid().ToString("N"));
        var manifest = new ModuleManifest
        {
            Id = "admission.fixture",
            Version = "1.0.0",
            EntryPoint = "fixture.exe",
            Name = new("name"),
        };

        var error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            ModuleWorkerClient.StartAsync(Path.Combine(directory, manifest.EntryPoint), directory,
                manifest, cancellation.Token));

        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsFalse(Directory.Exists(directory), "A canceled activation must not materialize worker data.");
    }
}
