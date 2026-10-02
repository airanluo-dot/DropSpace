using System.Diagnostics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LlamaCompletionRunnerTests
{
    [TestMethod]
    public async Task CleanupTimeoutRetainsExitObservationAndInferenceGate()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new SemaphoreSlim(0, 1);
        var release = LlamaCompletionRunner.ReleaseGateAfterCleanupAsync(cleanup.Task, gate);
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            LocalInferenceProcess.WaitForCleanupAsync(cleanup.Task, 123, TimeSpan.FromMilliseconds(20)));
        StringAssert.Contains(error.Message, "123");
        StringAssert.Contains(error.Message, "ownership and exit observation remain active");
        Assert.IsFalse(cleanup.Task.IsCompleted, "Timing out the caller must not cancel exit observation.");
        Assert.AreEqual(0, gate.CurrentCount);
        cleanup.SetResult();
        await release;
        Assert.AreEqual(1, gate.CurrentCount);
    }

    [TestMethod]
    public async Task CleanupTimeoutPreservesPrimaryFailureAndReportsItsOwnDiagnostic()
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new OperationCanceledException("Inference was cancelled.");
        await LlamaCompletionRunner.WaitForCleanupPreservingFailureAsync(cleanup.Task, 456, primary, TimeSpan.FromMilliseconds(20));
        Assert.IsInstanceOfType<string>(primary.Data["LocalInferenceShutdownFailure"]);
        StringAssert.Contains((string)primary.Data["LocalInferenceShutdownFailure"]!, "456");
        Assert.IsFalse(cleanup.Task.IsCompleted);
        cleanup.SetResult();
    }

    [TestMethod]
    public async Task FailedExitObservationDoesNotReleaseTheInferenceGate()
    {
        using var gate = new SemaphoreSlim(0, 1);
        await LlamaCompletionRunner.ReleaseGateAfterCleanupAsync(Task.FromException(new IOException("Exit could not be observed.")), gate);
        Assert.AreEqual(0, gate.CurrentCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationDoesNotReturnUntilTheStartedProcessHasExited(bool tokenizer)
    {
        WindowsProcessFixture.RequireAvailable();
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-runner-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "fake-runtime.exe" : "fake-runtime");
        var pidFile = Path.Combine(root, "process-id");
        var staging = Path.Combine(root, "prompts");
        using var runner = new LlamaCompletionRunner(() =>
        {
            Assert.IsFalse(tokenizer, "Vocabulary-only tokenization must not use full-model CPU RAM admission.");
            return TestInferenceMemory.Sufficient();
        });
        using var cancellation = new CancellationTokenSource();
        Task? running = null;
        try
        {
            if (OperatingSystem.IsWindows())
                WindowsProcessFixture.Write(executable, new { kind = "lifetime", pidPath = pidFile, delayMilliseconds = 60_000 });
            else
            {
                await File.WriteAllTextAsync(executable, $"#!/bin/sh\necho $$ > '{pidFile.Replace("'", "'\\''")}'\nexec /bin/sleep 60\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            running = tokenizer
                ? runner.CountTokensAsync(executable, Path.Combine(root, "model"), "fixture", staging, cancellation.Token)
                : runner.RunAsync(executable, Path.Combine(root, "model"), "fixture", staging, cancellation.Token);
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int processId;
            while (!File.Exists(pidFile) || !int.TryParse(await File.ReadAllTextAsync(pidFile, startupTimeout.Token), out processId))
            {
                if (running.IsCompleted) await running;
                await Task.Delay(10, startupTimeout.Token);
            }
            using var observed = Process.GetProcessById(processId);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => running);
            Assert.IsTrue(observed.HasExited, "Cancellation must await exit before returning ownership to the caller.");
            Assert.AreEqual(0, Directory.GetFiles(staging).Length);
        }
        finally
        {
            cancellation.Cancel();
            if (running is not null)
                try { await running; } catch (OperationCanceledException) { }
            await runner.DrainCleanupAsync(CancellationToken.None);
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void CompactAndStandardUseTheirSeparateApprovedMemoryBudgets()
    {
        Assert.AreEqual(1536L * 1024 * 1024, LlamaCompletionRunner.MemoryBudgetFor(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Compact.Sha256));
        Assert.AreEqual(3L * 1024 * 1024 * 1024, LlamaCompletionRunner.MemoryBudgetFor(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Standard.Sha256));
    }

    [TestMethod]
    public void CompactEosCorrectionIsWhitelistedToOneVerifiedHash()
    {
        Assert.HasCount(2, LlamaCompletionRunner.ModelCompatibilityArguments(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Compact.Sha256));
        Assert.AreEqual("tokenizer.ggml.eos_token_id=int:120020", LlamaCompletionRunner.ModelCompatibilityArguments(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Compact.Sha256.ToUpperInvariant())[1]);
        Assert.HasCount(0, LlamaCompletionRunner.ModelCompatibilityArguments(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Standard.Sha256));
        Assert.HasCount(0, LlamaCompletionRunner.ModelCompatibilityArguments(null));
        Assert.HasCount(0, LlamaCompletionRunner.ModelCompatibilityArguments("untrusted-file"));
    }

    [TestMethod]
    public void OnlyExactTrailingRuntimeMarkerIsRemoved()
    {
        Assert.AreEqual("[]", LlamaCompletionRunner.RemoveRuntimeTerminator("[] [end of text]\n\n"));
        const string withinText = "[{\"id\":0,\"text\":\"[end of text]\"}]";
        Assert.AreEqual(withinText, LlamaCompletionRunner.RemoveRuntimeTerminator(withinText));
        Assert.AreEqual("explanation []", LlamaCompletionRunner.RemoveRuntimeTerminator("explanation []"));
    }
}
