using System.ComponentModel;
using System.Diagnostics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class WindowsInferenceProcessTests
{
    [TestMethod]
    public async Task WatchdogFailurePreservesPrimaryErrorWithoutPoisoningReleasedOwnership()
    {
        var start = OperatingSystem.IsWindows()
            ? PowerShellStart("[Console]::WriteLine('ready'); [Threading.Thread]::Sleep(60000)")
            : new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-c", "printf 'ready\\n'; exec /bin/sleep 60" },
            };
        using var child = LocalInferenceProcess.Start(start);
        using var observed = Process.GetProcessById(child.Process.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.AreEqual("ready", await child.StandardOutput.ReadLineAsync(timeout.Token));
        var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = child.StandardError.ReadToEndAsync(timeout.Token);
        var primary = new Win32Exception(5, "Watchdog snapshot failed.");
        var watchdog = Task.FromException(primary);
        using var gate = new SemaphoreSlim(0, 1);
        var actual = await Assert.ThrowsExactlyAsync<Win32Exception>(async () =>
        {
            try { await watchdog; }
            finally
            {
                var cleanup = child.CompleteAsync(output, errors, watchdog);
                await LlamaCompletionRunner.WaitForCleanupPreservingFailureAsync(cleanup, observed.Id, primary);
                await LlamaCompletionRunner.ReleaseGateAfterCleanupAsync(cleanup, gate);
            }
        });
        Assert.AreSame(primary, actual);
        Assert.IsTrue(observed.HasExited);
        Assert.AreEqual(1, gate.CurrentCount, "A completed ownership barrier must release the inference gate despite a business-operation failure.");
        Assert.ThrowsExactly<ObjectDisposedException>(() => child.StandardOutput.Peek());
        Assert.ThrowsExactly<ObjectDisposedException>(() => child.StandardError.Peek());
        Assert.IsFalse(primary.Data.Contains("LocalInferenceShutdownFailure"));
    }

    [TestMethod]
    public async Task NativeProcessReadsClosedPromptAndRedirectsBothStreams()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the native Windows process APIs."); return; }
        var directory = Path.Combine(Path.GetTempPath(), "DropSpace native prompt " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var prompt = Path.Combine(directory, "prompt.txt");
        try
        {
            await File.WriteAllTextAsync(prompt, "native-prompt-readable");
            using var child = LocalInferenceProcess.Start(PowerShellStart(
                $"[Console]::Write([IO.File]::ReadAllText('{prompt.Replace("'", "''")}')); [Console]::Error.Write('stderr-readable')"));
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await child.Process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, child.Process.ExitCode);
            Assert.AreEqual("native-prompt-readable", await output);
            Assert.AreEqual("stderr-readable", await errors);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task NativeCommittedMemoryCannotExceedThreeGiB()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires native Windows job memory enforcement."); return; }
        using var child = LocalInferenceProcess.Start(PowerShellStart(
            "try { $p = [Runtime.InteropServices.Marshal]::AllocHGlobal([IntPtr]4294967296); " +
            "[Runtime.InteropServices.Marshal]::FreeHGlobal($p); exit 3 } " +
            "catch { if ($_.Exception.InnerException -is [OutOfMemoryException]) { [Console]::Write('limited'); exit 0 }; throw }"));
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await child.Process.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(0, child.Process.ExitCode);
        Assert.AreEqual("limited", await output);
        Assert.AreEqual(string.Empty, await errors);
    }

    [TestMethod]
    public void DisposingOwnerWaitsForTheNativeChildToExit()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the native Windows process APIs."); return; }
        var child = LocalInferenceProcess.Start(PowerShellStart("[Threading.Thread]::Sleep(60000)"));
        using var observed = Process.GetProcessById(child.Process.Id);
        child.Dispose();
        Assert.IsTrue(observed.HasExited, "Owner disposal must wait for exit, not merely request termination.");
    }

    [TestMethod]
    public async Task AsyncTerminationWaitsForExitAndDrainsBothNativeStreams()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the native Windows process APIs."); return; }
        using var child = LocalInferenceProcess.Start(PowerShellStart(
            "[Console]::Error.Write('stderr-readable'); [Console]::WriteLine('ready'); [Threading.Thread]::Sleep(60000)"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.AreEqual("ready", await child.StandardOutput.ReadLineAsync(timeout.Token));
        var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = child.StandardError.ReadToEndAsync(timeout.Token);
        using var observed = Process.GetProcessById(child.Process.Id);
        await LocalInferenceProcess.WaitForCleanupAsync(child.CompleteAsync(output, errors), observed.Id);
        Assert.IsTrue(observed.HasExited, "Async termination must await the OS exit barrier.");
        Assert.AreEqual(string.Empty, await output);
        Assert.AreEqual("stderr-readable", await errors);
    }

    [TestMethod]
    public async Task NativeExitSignalObservationSurvivesACallerTimeout()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the native Windows process exit signal."); return; }
        using var child = LocalInferenceProcess.Start(PowerShellStart(
            "[Console]::WriteLine('ready'); [Threading.Thread]::Sleep(60000)"));
        using var observed = Process.GetProcessById(child.Process.Id);
        _ = observed.Handle;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.AreEqual("ready", await child.StandardOutput.ReadLineAsync(timeout.Token));
        var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = child.StandardError.ReadToEndAsync(timeout.Token);
        var exit = WindowsInferenceProcess.WaitForExitSignalAsync(child.Process.SafeHandle);
        Assert.IsFalse(exit.IsCompleted);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            LocalInferenceProcess.WaitForCleanupAsync(exit, observed.Id, TimeSpan.FromMilliseconds(20)));
        Assert.IsFalse(exit.IsCompleted, "A caller timeout must leave actual process exit observation alive.");
        Assert.IsFalse(observed.WaitForExit(0));
        await LocalInferenceProcess.WaitForCleanupAsync(child.CompleteAsync(output, errors), observed.Id);
        await exit.WaitAsync(timeout.Token);
        Assert.IsTrue(observed.WaitForExit(0), "Cleanup must reach the kernel signal without another blocking wait.");
    }

    [TestMethod]
    public void WindowsArgumentQuotingPreservesQuotesAndTrailingSlashes()
    {
        Assert.AreEqual("\"\"", WindowsInferenceProcess.QuoteArgument(string.Empty));
        Assert.AreEqual("\"hello world\"", WindowsInferenceProcess.QuoteArgument("hello world"));
        Assert.AreEqual("\"a\\\"b\"", WindowsInferenceProcess.QuoteArgument("a\"b"));
        Assert.AreEqual("\"C:\\with spaces\\\\\"", WindowsInferenceProcess.QuoteArgument("C:\\with spaces\\"));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsInferenceProcess.QuoteArgument("bad\0value"));
    }

    private static ProcessStartInfo PowerShellStart(string command)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command })
            start.ArgumentList.Add(argument);
        return start;
    }
}
