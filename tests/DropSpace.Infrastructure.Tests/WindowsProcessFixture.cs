using System.Text.Json;

namespace DropSpace.Infrastructure.Tests;

// This installs a synthetic apphost, not a mock of the process owner or runner.
// Every caller still uses production CreateProcess/Job/sharing/pipe/cleanup code.
internal static class WindowsProcessFixture
{
    internal static void RequireAvailable()
    {
        if (OperatingSystem.IsWindows())
            Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "ResidentFixture", "DropSpace.ResidentWorkerFixture.exe")),
                "The native Windows process fixture must be built; its absence is a failure, not a skip.");
    }

    internal static void Write(string executable, object scenario)
    {
        RequireAvailable();
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.IsTrue(directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), "Fixtures must stay in their test-owned temporary tree.");
        var payload = Path.Combine(AppContext.BaseDirectory, "ResidentFixture");
        foreach (var file in Directory.EnumerateFiles(payload, "DropSpace.ResidentWorkerFixture.*"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        File.Copy(Path.Combine(payload, "DropSpace.ResidentWorkerFixture.exe"), executable, overwrite: true);
        File.WriteAllBytes(Path.Combine(directory, "process-fixture.json"), JsonSerializer.SerializeToUtf8Bytes(scenario));
    }
}
