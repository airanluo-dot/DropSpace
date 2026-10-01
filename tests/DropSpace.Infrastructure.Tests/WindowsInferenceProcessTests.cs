using System.Diagnostics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class WindowsInferenceProcessTests
{
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
    public async Task DisposingOwnerTerminatesTheNativeChild()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires the native Windows process APIs."); return; }
        var child = LocalInferenceProcess.Start(PowerShellStart("[Threading.Thread]::Sleep(60000)"));
        using var observed = Process.GetProcessById(child.Process.Id);
        child.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await observed.WaitForExitAsync(timeout.Token);
        Assert.IsTrue(observed.HasExited);
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
