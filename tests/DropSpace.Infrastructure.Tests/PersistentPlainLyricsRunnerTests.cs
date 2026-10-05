using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

/// <summary>Process protocol fixtures cover managed/OS ownership; they do not certify model or GPU inference.</summary>
[TestClass]
[DoNotParallelize]
public sealed class PersistentPlainLyricsRunnerTests
{
    [TestMethod]
    public async Task IndependentSelectionPreparationReapsTranslationResidentBeforeReplacingAndSwitchingBack()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner();
        using var previousSong = new CancellationTokenSource();
        Assert.AreEqual("translation", await fixture.RunAsync(runner, "translation", previousSong.Token));
        var translationPid = fixture.StartedProcesses().Single().Pid;
        fixture.BeforeResolve = _ => Assert.IsFalse(IsAlive(translationPid),
            "The preceding translation resident must exit before selector resolution.");
        var selectionModel = AiLyricsModelCatalog.ExperimentalLargePlain;
        Assert.IsTrue(await runner.PrepareSelectionAsync(fixture.Model, selectionModel.Sha256, default));
        var selection = fixture.StartedProcesses()[1];
        Assert.AreEqual("hy-mt2-7b-q8", selection.ModelProfile);
        Assert.IsTrue(runner.IsSelectionWarm(selectionModel.Sha256));
        Assert.IsFalse(runner.IsSelectionWarm(AiLyricsModelCatalog.ExperimentalPlain.Sha256));
        previousSong.Cancel();
        Assert.IsTrue(IsAlive(selection.Pid), "A retired translation token cannot kill the replacement selector.");
        Assert.AreEqual("metadata", await runner.TryRunSelectionAsync(selectionModel.Sha256, "metadata", default));
        Assert.IsTrue(await runner.PrepareSelectionAsync(fixture.Model, selectionModel.Sha256, default));
        Assert.HasCount(2, fixture.StartedProcesses(), "Preparing the same ready profile reuses its owner.");
        fixture.BeforeResolve = _ => Assert.IsFalse(IsAlive(selection.Pid),
            "The selector must exit before the next translation resident resolves.");
        Assert.AreEqual("translation again", await fixture.RunAsync(runner, "translation again"));
        Assert.HasCount(3, fixture.StartedProcesses());
        Assert.IsTrue(IsAlive(fixture.StartedProcesses()[2].Pid));
        Assert.AreEqual(0, LocalInferenceProcess.InferenceGate.CurrentCount,
            "The replacement retains the single shared gate while resident.");
    }

    [TestMethod]
    public async Task IndependentSelectionPreparationCannotReplaceActiveTranslation()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner();
        using var cancellation = new CancellationTokenSource();
        var translating = fixture.RunAsync(runner, "partial-and-block", cancellation.Token);
        await WaitUntilAsync(() => File.Exists(fixture.Blocked), translating);
        var translationPid = fixture.StartedProcesses().Single().Pid;
        Assert.IsFalse(await runner.PrepareSelectionAsync(fixture.Model,
            AiLyricsModelCatalog.ExperimentalLargePlain.Sha256, default));
        Assert.HasCount(1, fixture.StartedProcesses());
        Assert.IsTrue(IsAlive(translationPid));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => translating);
        Assert.IsFalse(IsAlive(translationPid));
    }

    [TestMethod]
    [DataRow(false, "physical")]
    [DataRow(false, "commit")]
    [DataRow(false, "unknown")]
    [DataRow(true, "physical")]
    [DataRow(true, "commit")]
    [DataRow(true, "unknown")]
    public async Task CpuAdmissionRejectsBeforeProcessStartAndRecoversAtTheSelectedModelBoundary(bool large, string missing)
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var model = large ? AiLyricsModelCatalog.ExperimentalLargePlain : AiLyricsModelCatalog.ExperimentalPlain;
        var required = (large ? 13L : 4L) << 30;
        CpuMemorySnapshot? memory = missing switch
        {
            "physical" => new(required - 1, required),
            "commit" => new(required, required - 1),
            _ => null,
        };
        var reads = 0;
        var runner = fixture.CreateRunner(readMemorySnapshot: () =>
        {
            Assert.AreEqual(0, LocalInferenceProcess.InferenceGate.CurrentCount);
            reads++;
            return memory;
        });
        await Assert.ThrowsExactlyAsync<InferenceResourcesUnavailableException>(() => fixture.RunAsync(runner, "source", model: model));
        Assert.AreEqual(1, reads);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "starts")), "Insufficient or unknown memory may not start a CPU child.");
        Assert.IsNull(runner.LastExecutionBackend);
        Assert.IsFalse(runner.LastExecutionUsedCpuFallback);
        Assert.AreEqual(1, LocalInferenceProcess.InferenceGate.CurrentCount, "Rejected startup releases admission.");

        memory = new(required, required);
        Assert.AreEqual("recovered", await fixture.RunAsync(runner, "recovered", model: model));
        Assert.AreEqual(2, reads);
        var pid = fixture.StartedProcesses().Single().Pid;
        memory = new(0, 0);
        Assert.AreEqual("resident reuse", await fixture.RunAsync(runner, "resident reuse", model: model));
        Assert.AreEqual(2, reads, "An already allocated resident does not require a second full model's free memory.");
        Assert.AreEqual(pid, fixture.StartedProcesses().Single().Pid);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task GpuFailureIsReapedBeforeFreshCpuAdmissionAndLowOrUnknownRamCannotStartFallback(bool large, bool unknown)
    {
        RequireFixture();
        await using var fixture = new Fixture();
        File.WriteAllText(fixture.FailGpuStartup, string.Empty);
        var model = large ? AiLyricsModelCatalog.ExperimentalLargePlain : AiLyricsModelCatalog.ExperimentalPlain;
        var required = (large ? 13L : 4L) << 30;
        CpuMemorySnapshot? memory = new(required, required);
        var reads = 0;
        var runner = fixture.CreateRunner(new AiLyricsRuntimeOptions { GpuEnabled = true }, readMemorySnapshot: () =>
        {
            reads++;
            var failed = fixture.StartedProcesses().Single();
            Assert.AreEqual("vulkan", failed.Mode);
            Assert.IsFalse(IsAlive(failed.Pid), "The GPU must actually exit before taking the fallback RAM snapshot.");
            Assert.AreEqual(0, LocalInferenceProcess.InferenceGate.CurrentCount);
            return memory;
        });
        fixture.BeforeResolve = gpu =>
        {
            if (!gpu) memory = unknown ? null : new(required - 1, required);
            Assert.AreEqual(0, reads, "GPU execution must not read or reuse a CPU admission snapshot.");
        };
        await Assert.ThrowsExactlyAsync<InferenceResourcesUnavailableException>(() => fixture.RunAsync(runner, "source", model: model));
        Assert.AreEqual(1, reads);
        Assert.HasCount(1, fixture.StartedProcesses(), "Only the failed, already reaped GPU process may exist.");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "requests")));
        CollectionAssert.AreEqual(new[] { true, false }, fixture.ResolvedModes);
        Assert.IsNull(runner.LastExecutionBackend);
        fixture.BeforeResolve = null;
        memory = new(required, required);
        Assert.AreEqual("retry after RAM recovery", await fixture.RunAsync(runner, "retry after RAM recovery", model: model));
        Assert.AreEqual(2, reads);
        Assert.HasCount(2, fixture.StartedProcesses());
        Assert.AreEqual("cpu", runner.LastExecutionBackend);
        Assert.IsTrue(runner.LastExecutionUsedCpuFallback);
    }

    [TestMethod]
    public async Task ModelSwitchRechecksRamAfterOldResidentExitAndCannotReuseSmallModelAdmission()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var reads = 0;
        int? previousPid = null;
        var runner = fixture.CreateRunner(readMemorySnapshot: () =>
        {
            reads++;
            if (previousPid is { } pid) Assert.IsFalse(IsAlive(pid));
            return new(4L << 30, 4L << 30);
        });
        Assert.AreEqual("small", await fixture.RunAsync(runner, "small"));
        previousPid = fixture.StartedProcesses().Single().Pid;
        await Assert.ThrowsExactlyAsync<InferenceResourcesUnavailableException>(() =>
            fixture.RunAsync(runner, "large", model: AiLyricsModelCatalog.ExperimentalLargePlain));
        Assert.AreEqual(2, reads);
        Assert.HasCount(1, fixture.StartedProcesses());
        Assert.IsFalse(IsAlive(previousPid.Value));
    }

    [TestMethod]
    public async Task CancellationWhileWaitingForGlobalAdmissionDoesNotReadRamOrStartProcess()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var reads = 0;
        var runner = fixture.CreateRunner(readMemorySnapshot: () => { reads++; return TestInferenceMemory.Sufficient(); });
        await LocalInferenceProcess.InferenceGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var pending = fixture.RunAsync(runner, "queued", cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            Assert.AreEqual(0, reads);
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "starts")));
        }
        finally { LocalInferenceProcess.InferenceGate.Release(); }
    }

    [TestMethod]
    public async Task ModelSwitchDrainsResidentAndUsesIndependentProfileEvenWhenPathIsReused()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner();
        Assert.AreEqual("small", await fixture.RunAsync(runner, "small"));
        var smallPid = fixture.StartedProcesses().Single().Pid;
        fixture.BeforeResolve = _ => Assert.IsFalse(IsAlive(smallPid), "Model replacement must wait for prior native exit.");
        Assert.AreEqual("large", await fixture.RunAsync(runner, "large", model: AiLyricsModelCatalog.ExperimentalLargePlain));
        var large = fixture.StartedProcesses()[1];
        Assert.AreEqual("hy-mt2-7b-q8", large.ModelProfile);
        Assert.AreNotEqual(smallPid, large.Pid);
        Assert.AreEqual("large again", await fixture.RunAsync(runner, "large again", model: AiLyricsModelCatalog.ExperimentalLargePlain));
        Assert.HasCount(2, fixture.StartedProcesses());
        fixture.BeforeResolve = _ => Assert.IsFalse(IsAlive(large.Pid));
        Assert.AreEqual("small again", await fixture.RunAsync(runner, "small again"));
        Assert.HasCount(3, fixture.StartedProcesses());
        Assert.AreEqual("hy-mt2-1.8b-q8", fixture.StartedProcesses()[2].ModelProfile);
        Assert.HasCount(4, PersistentPlainLyricsRunner.BuildArguments(fixture.Model, false));
        Assert.HasCount(6, PersistentPlainLyricsRunner.BuildArguments(fixture.Model, false, AiLyricsModelCatalog.ExperimentalLargePlain.Sha256));
    }

    [TestMethod]
    [DataRow("omit-model-profile")]
    [DataRow("wrong-model-profile")]
    public async Task LargeModelRejectsOldOrMismatchedWorkerHandshakeBeforeSendingPrompt(string marker)
    {
        RequireFixture();
        await using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, marker), string.Empty);
        var runner = fixture.CreateRunner(new AiLyricsRuntimeOptions { GpuEnabled = true });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync(runner, "must not reach worker", model: AiLyricsModelCatalog.ExperimentalLargePlain));
        Assert.IsFalse(IsAlive(fixture.StartedProcesses().Single().Pid));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "requests")));
        CollectionAssert.AreEqual(new[] { true }, fixture.ResolvedModes);
    }

    [TestMethod]
    public async Task LargeModelCancellationDrainsBeforeReturningToDefaultProfile()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner();
        using var cancel = new CancellationTokenSource();
        var pending = fixture.RunAsync(runner, "partial-and-block", cancel.Token, AiLyricsModelCatalog.ExperimentalLargePlain);
        await WaitUntilAsync(() => File.Exists(fixture.Blocked), pending);
        var largePid = fixture.StartedProcesses().Single().Pid;
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsFalse(IsAlive(largePid));
        Assert.AreEqual("default restored", await fixture.RunAsync(runner, "default restored"));
        Assert.AreEqual("hy-mt2-1.8b-q8", fixture.StartedProcesses()[1].ModelProfile);
    }

    [TestMethod]
    public async Task ConsecutiveRequestsReuseProcessAndKeepHostIdsOutsidePrompt()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner();
        Assert.IsNull(runner.LastExecutionBackend);
        Assert.IsFalse(runner.LastExecutionUsedCpuFallback);
        const string first = "Translate into Chinese:\n夜空 / 달빛 / a quiet night";
        Assert.AreEqual(first, await fixture.RunAsync(runner, first));
        Assert.AreEqual("cpu", runner.LastExecutionBackend);
        Assert.IsFalse(runner.LastExecutionUsedCpuFallback);
        var pid = fixture.StartedProcesses().Single().Pid;
        Assert.IsTrue(IsAlive(pid), "Successful output must leave the resident model process reusable.");
        Assert.AreEqual("next line", await fixture.RunAsync(runner, "next line"));
        Assert.HasCount(1, fixture.StartedProcesses());
        var requests = fixture.Requests();
        Assert.HasCount(2, requests);
        Assert.AreEqual(first, requests[0].Prompt);
        Assert.AreEqual("next line", requests[1].Prompt);
        Assert.AreNotEqual(requests[0].Id, requests[1].Id);
        Assert.IsTrue(requests.All(request => request.Id.Length == 32 && request.Pid == pid && request.Protocol == 1));
        Assert.IsFalse(Directory.Exists(fixture.Staging), "Resident requests must not stage plaintext prompt files.");
    }

    [TestMethod]
    public async Task MismatchedResponseIdKillsWorkerWithoutGpuFallbackAndNextRequestStartsCleanly()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner(new AiLyricsRuntimeOptions { GpuEnabled = true });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync(runner, "mismatched-id"));
        Assert.IsNull(runner.LastExecutionBackend, "Rejected output must not be reported as completed backend evidence.");
        Assert.IsFalse(runner.LastExecutionUsedCpuFallback);
        var rejected = fixture.StartedProcesses().Single();
        Assert.AreEqual("vulkan", rejected.Mode);
        Assert.IsFalse(IsAlive(rejected.Pid), "A mismatched host ID must be discarded and its process reaped.");
        CollectionAssert.AreEqual(new[] { true }, fixture.ResolvedModes);
        Assert.AreEqual("fresh", await fixture.RunAsync(runner, "fresh"));
        var restarted = fixture.StartedProcesses();
        Assert.HasCount(2, restarted);
        Assert.AreNotEqual(rejected.Pid, restarted[1].Pid);
        Assert.AreEqual("vulkan", restarted[1].Mode);
        Assert.AreEqual("vulkan", runner.LastExecutionBackend);
        Assert.IsFalse(runner.LastExecutionUsedCpuFallback);
    }

    [TestMethod]
    [DataRow("wrong-protocol")]
    [DataRow("incomplete-response")]
    [DataRow("oversized-output")]
    public async Task InvalidCompletedResponseIsReapedWithoutRetryingOnCpu(string prompt)
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner(new AiLyricsRuntimeOptions { GpuEnabled = true });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync(runner, prompt));
        Assert.IsFalse(IsAlive(fixture.StartedProcesses().Single().Pid));
        CollectionAssert.AreEqual(new[] { true }, fixture.ResolvedModes);
        Assert.IsNull(runner.LastExecutionBackend);
    }

    [TestMethod]
    public async Task CancellationDiscardsPartialOutputAndReapsGpuBeforeNextCpuResolution()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var options = new AiLyricsRuntimeOptions { GpuEnabled = true };
        var runner = fixture.CreateRunner(options);
        using var cancel = new CancellationTokenSource();
        var pending = fixture.RunAsync(runner, "partial-and-block", cancel.Token);
        await WaitUntilAsync(() => File.Exists(fixture.Blocked), pending);
        var gpu = fixture.StartedProcesses().Single();
        Assert.IsTrue(IsAlive(gpu.Pid));
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.IsFalse(IsAlive(gpu.Pid), "Cancellation must await actual child exit before returning.");
        CollectionAssert.AreEqual(new[] { true }, fixture.ResolvedModes,
            "A canceled GPU request must never launch an automatic CPU retry.");
        options.GpuEnabled = false;
        fixture.BeforeResolve = _ => Assert.IsFalse(IsAlive(gpu.Pid), "The previous GPU must be gone before CPU resolution.");
        Assert.AreEqual("after cancellation", await fixture.RunAsync(runner, "after cancellation"));
        CollectionAssert.AreEqual(new[] { true, false }, fixture.ResolvedModes);
        Assert.AreEqual("cpu", fixture.StartedProcesses()[1].Mode);
    }

    [TestMethod]
    public async Task FailedGpuStartupIsReapedBeforeSingleCpuRetryAndFallbackRemainsResident()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        File.WriteAllText(fixture.FailGpuStartup, string.Empty);
        var runner = fixture.CreateRunner(new AiLyricsRuntimeOptions { GpuEnabled = true });
        fixture.BeforeResolve = gpu =>
        {
            if (gpu) return;
            var failed = fixture.StartedProcesses().Single();
            Assert.AreEqual("vulkan", failed.Mode);
            Assert.IsFalse(IsAlive(failed.Pid), "CPU fallback may not resolve until the failed GPU child is reaped.");
        };
        Assert.AreEqual("first", await fixture.RunAsync(runner, "first"));
        Assert.AreEqual("second", await fixture.RunAsync(runner, "second"));
        Assert.AreEqual("cpu", runner.LastExecutionBackend);
        Assert.IsTrue(runner.LastExecutionUsedCpuFallback);
        CollectionAssert.AreEqual(new[] { true, false }, fixture.ResolvedModes);
        var starts = fixture.StartedProcesses();
        Assert.HasCount(2, starts);
        Assert.AreEqual("cpu", starts[1].Mode);
        Assert.IsTrue(IsAlive(starts[1].Pid));
        Assert.IsTrue(fixture.Requests().All(request => request.Pid == starts[1].Pid));
    }

    [TestMethod]
    public async Task GpuOffNeverResolvesGpuAndDrainsBeforeEitherModeChange()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var options = new AiLyricsRuntimeOptions { GpuEnabled = false };
        var runner = fixture.CreateRunner(options);
        Assert.AreEqual("cpu one", await fixture.RunAsync(runner, "cpu one"));
        Assert.AreEqual("cpu two", await fixture.RunAsync(runner, "cpu two"));
        CollectionAssert.AreEqual(new[] { false }, fixture.ResolvedModes);
        var cpu = fixture.StartedProcesses().Single().Pid;
        fixture.BeforeResolve = _ => Assert.IsFalse(IsAlive(cpu));
        options.GpuEnabled = true;
        Assert.AreEqual("gpu", await fixture.RunAsync(runner, "gpu"));
        Assert.AreEqual("vulkan", runner.LastExecutionBackend);
        Assert.IsFalse(runner.LastExecutionUsedCpuFallback);
        var gpu = fixture.StartedProcesses()[1].Pid;
        Assert.IsTrue(IsAlive(gpu));
        fixture.BeforeResolve = _ => Assert.IsFalse(IsAlive(gpu));
        options.GpuEnabled = false;
        Assert.AreEqual("cpu again", await fixture.RunAsync(runner, "cpu again"));
        Assert.AreEqual("cpu", runner.LastExecutionBackend);
        Assert.IsFalse(runner.LastExecutionUsedCpuFallback);
        CollectionAssert.AreEqual(new[] { false, true, false }, fixture.ResolvedModes);
        CollectionAssert.AreEqual(new[] { "cpu", "vulkan", "cpu" }, fixture.StartedProcesses().Select(start => start.Mode).ToArray());
    }

    [TestMethod]
    public async Task IdleTimeoutReapsWorkerReleasesSharedGateAndNextRequestRestarts()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner(idleTimeout: TimeSpan.FromMilliseconds(100));
        Assert.AreEqual("first", await fixture.RunAsync(runner, "first"));
        var first = fixture.StartedProcesses().Single().Pid;
        await WaitUntilAsync(() => !IsAlive(first));
        Assert.IsTrue(await LocalInferenceProcess.InferenceGate.WaitAsync(TimeSpan.FromSeconds(3)),
            "Idle cleanup must release admission for the other local inference backends.");
        LocalInferenceProcess.InferenceGate.Release();
        Assert.AreEqual("after idle", await fixture.RunAsync(runner, "after idle"));
        Assert.HasCount(2, fixture.StartedProcesses());
        Assert.AreNotEqual(first, fixture.StartedProcesses()[1].Pid);
    }

    [TestMethod]
    public async Task ExplicitDrainReapsResidentWorkerAndAllowsReuse()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner();
        Assert.AreEqual("before drain", await fixture.RunAsync(runner, "before drain"));
        var first = fixture.StartedProcesses().Single().Pid;
        await runner.DrainCleanupAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(IsAlive(first));
        await runner.DrainCleanupAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("after drain", await fixture.RunAsync(runner, "after drain"));
        Assert.HasCount(2, fixture.StartedProcesses());
    }

    [TestMethod]
    public async Task SongCancellationAfterCompletedResponseReapsIdleWorkerBeforeTimeout()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner(idleTimeout: TimeSpan.FromMinutes(2));
        using var song = new CancellationTokenSource();
        Assert.AreEqual("finished line", await fixture.RunAsync(runner, "finished line", song.Token));
        var pid = fixture.StartedProcesses().Single().Pid;
        Assert.IsTrue(IsAlive(pid));
        song.Cancel();
        await WaitUntilAsync(() => !IsAlive(pid));
        await runner.DrainCleanupAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(IsAlive(pid));
        Assert.AreEqual("new song", await fixture.RunAsync(runner, "new song"));
        Assert.HasCount(2, fixture.StartedProcesses());
    }

    [TestMethod]
    public async Task ReplacedSongTokenCannotKillWorkerOwnedByNewRequest()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner(idleTimeout: TimeSpan.FromMinutes(2));
        using var oldSong = new CancellationTokenSource();
        using var newSong = new CancellationTokenSource();
        Assert.AreEqual("old song", await fixture.RunAsync(runner, "old song", oldSong.Token));
        var pid = fixture.StartedProcesses().Single().Pid;
        Assert.AreEqual("new song", await fixture.RunAsync(runner, "new song", newSong.Token));
        oldSong.Cancel();
        Assert.IsTrue(IsAlive(pid));
        Assert.AreEqual("new song next line", await fixture.RunAsync(runner, "new song next line", newSong.Token));
        Assert.HasCount(1, fixture.StartedProcesses());
        newSong.Cancel();
        await WaitUntilAsync(() => !IsAlive(pid));
    }

    [TestMethod]
    public async Task DisposeCancelsActiveRequestAndDrainConfirmsExitBeforeRejectingReuse()
    {
        RequireFixture();
        await using var fixture = new Fixture();
        var runner = fixture.CreateRunner();
        var pending = fixture.RunAsync(runner, "partial-and-block");
        await WaitUntilAsync(() => File.Exists(fixture.Blocked), pending);
        var pid = fixture.StartedProcesses().Single().Pid;
        runner.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await runner.DrainCleanupAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(IsAlive(pid));
        runner.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => fixture.RunAsync(runner, "after dispose"));
        Assert.HasCount(1, fixture.ResolvedModes);
    }

    [TestMethod]
    public async Task InvalidModelPromptAndPrecancelledCallFailBeforeResolvingAnyExecutable()
    {
        await using var fixture = new Fixture(writeRuntime: false);
        var runner = fixture.CreateRunner();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => runner.RunPlainAsync("unused", fixture.Model, "source",
            fixture.Staging, CancellationToken.None, "unverified"));
        foreach (var prompt in new[] { string.Empty, new string('夜', PlainHyLyricsProtocol.MaximumPromptBytes) })
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.RunAsync(runner, prompt));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.RunAsync(runner, "source", cancel.Token));
        Assert.HasCount(0, fixture.ResolvedModes);
        Assert.IsFalse(Directory.Exists(fixture.Staging));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WorkerArgumentsContainOnlyCanonicalModelAndBackend(bool gpu)
    {
        var model = Path.Combine(Path.GetTempPath(), "model with spaces.gguf");
        CollectionAssert.AreEqual(new[] { "--model", Path.GetFullPath(model), "--mode", gpu ? "vulkan" : "cpu" },
            PersistentPlainLyricsRunner.BuildArguments(model, gpu).ToArray());
    }

    [TestMethod]
    public void RuntimeOptionsAllowGpuByDefaultAndCanExplicitlyDisableIt()
    {
        var options = new AiLyricsRuntimeOptions();
        Assert.IsTrue(options.GpuEnabled);
        options.GpuEnabled = false;
        Assert.IsFalse(options.GpuEnabled);
    }

    private static void RequireFixture()
    {
        if (!OperatingSystem.IsWindows() && !File.Exists("/usr/bin/python3"))
            Assert.Inconclusive("The POSIX fixture requires /usr/bin/python3; Windows uses the built managed fixture.");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitUntilAsync(Func<bool> ready, Task? running = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready())
        {
            if (running?.IsCompleted == true)
            {
                await running;
                Assert.Fail("The fixture request completed before reaching the expected state.");
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed record StartedProcess(int Pid, string Mode, string ModelProfile);
    private sealed record Request(int Protocol, int Pid, string Id, string Prompt);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly List<PersistentPlainLyricsRunner> _runners = [];

        internal Fixture(bool writeRuntime = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "DropSpace-resident-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            if (!writeRuntime) return;
            if (OperatingSystem.IsWindows())
            {
                var payload = Path.Combine(AppContext.BaseDirectory, "ResidentFixture");
                var appHost = Path.Combine(payload, "DropSpace.ResidentWorkerFixture.exe");
                Assert.IsTrue(File.Exists(appHost), "The Windows resident fixture must be built; missing fixtures are not skipped.");
                foreach (var file in Directory.EnumerateFiles(payload, "DropSpace.ResidentWorkerFixture.*"))
                    File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
                File.Copy(appHost, Executable);
                return;
            }
            File.WriteAllText(Executable, "#!/usr/bin/python3\n" + Runtime, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        internal string Root { get; }
        internal string Executable => Path.Combine(Root, OperatingSystem.IsWindows() ? "fake resident worker.exe" : "fake resident worker");
        internal string Model => Path.Combine(Root, "Hy model.gguf");
        internal string Staging => Path.Combine(Root, "private prompts");
        internal string Blocked => Path.Combine(Root, "blocked");
        internal string FailGpuStartup => Path.Combine(Root, "fail-vulkan-startup");
        internal List<bool> ResolvedModes { get; } = [];
        internal Action<bool>? BeforeResolve { get; set; }

        internal PersistentPlainLyricsRunner CreateRunner(AiLyricsRuntimeOptions? options = null, TimeSpan? idleTimeout = null,
            Func<CpuMemorySnapshot?>? readMemorySnapshot = null)
        {
            var runner = new PersistentPlainLyricsRunner((gpu, token) =>
            {
                token.ThrowIfCancellationRequested();
                BeforeResolve?.Invoke(gpu);
                ResolvedModes.Add(gpu);
                return Task.FromResult(Executable);
            }, options ?? new AiLyricsRuntimeOptions { GpuEnabled = false }, idleTimeout ?? TimeSpan.FromSeconds(5),
                readMemorySnapshot ?? TestInferenceMemory.Sufficient);
            _runners.Add(runner);
            return runner;
        }

        internal Task<string> RunAsync(PersistentPlainLyricsRunner runner, string prompt, CancellationToken token = default,
            AiLyricsModelDescriptor? model = null) =>
            runner.RunPlainAsync(Path.Combine(Root, "unused one-shot executable"), Model, prompt, Staging, token,
                (model ?? AiLyricsModelCatalog.ExperimentalPlain).Sha256).WaitAsync(TimeSpan.FromSeconds(15));

        internal StartedProcess[] StartedProcesses() => ReadEvents("starts").Select(item => new StartedProcess(
            item.GetProperty("pid").GetInt32(), item.GetProperty("mode").GetString()!, item.GetProperty("modelProfile").GetString()!)).ToArray();

        internal Request[] Requests() => ReadEvents("requests").Select(item => new Request(
            item.GetProperty("protocol").GetInt32(), item.GetProperty("pid").GetInt32(),
            item.GetProperty("id").GetString()!, item.GetProperty("prompt").GetString()!)).ToArray();

        private JsonElement[] ReadEvents(string name) => File.ReadAllLines(Path.Combine(Root, name)).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

        public async ValueTask DisposeAsync()
        {
            foreach (var runner in _runners) runner.Dispose();
            foreach (var runner in _runners)
                await runner.DrainCleanupAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            Directory.Delete(Root, recursive: true);
        }

        private const string Runtime = """
            import json, os, sys, time
            root = os.path.dirname(os.path.abspath(__file__))
            mode = sys.argv[sys.argv.index('--mode') + 1]
            model_profile = sys.argv[sys.argv.index('--model-profile') + 1] if '--model-profile' in sys.argv else 'hy-mt2-1.8b-q8'
            pid = os.getpid()
            def record(name, value):
                with open(os.path.join(root, name), 'a', encoding='utf-8') as output:
                    output.write(json.dumps(value, ensure_ascii=False) + '\n')
            record('starts', {'pid': pid, 'mode': mode, 'modelProfile': model_profile})
            if mode == 'vulkan' and os.path.exists(os.path.join(root, 'fail-vulkan-startup')):
                os.close(1)
                time.sleep(60)
                sys.exit(27)
            ready = {'protocol': 1, 'ready': True, 'backend': mode, 'selectionProtocol': 2}
            if not os.path.exists(os.path.join(root, 'omit-model-profile')):
                ready['modelProfile'] = 'wrong-model' if os.path.exists(os.path.join(root, 'wrong-model-profile')) else model_profile
            print(json.dumps(ready), flush=True)
            for line in sys.stdin:
                request = json.loads(line)
                record('requests', dict(request, pid=pid))
                if request['prompt'] == 'partial-and-block':
                    sys.stdout.write('{"protocol":1,"text":"private incomplete output')
                    sys.stdout.flush()
                    open(os.path.join(root, 'blocked'), 'w').close()
                    time.sleep(60)
                else:
                    response_id = 'wrong-host-request-id' if request['prompt'] == 'mismatched-id' else request['id']
                    protocol = 2 if request['prompt'] == 'wrong-protocol' else request['protocol']
                    complete = request['prompt'] != 'incomplete-response'
                    text = '夜' * 6000 if request['prompt'] == 'oversized-output' else request['prompt'] + ' [end of text]'
                    print(json.dumps({'protocol': protocol, 'id': response_id, 'complete': complete,
                                      'text': text}, ensure_ascii=False), flush=True)
            """;
    }
}
