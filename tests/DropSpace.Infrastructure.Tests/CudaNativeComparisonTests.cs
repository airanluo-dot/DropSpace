using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

/// <summary>Opt-in native evidence, never synthetic performance. Uses installed GGUFs and owned Windows Jobs.</summary>
[TestClass]
[DoNotParallelize]
public sealed class CudaNativeComparisonTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("CudaNativeExperiment")]
    [DataRow("cpu", "hy-mt2-18-q8-plain-beta", "DROPSPACE_CUDA_MODEL")]
    [DataRow("vulkan", "hy-mt2-18-q8-plain-beta", "DROPSPACE_CUDA_MODEL")]
    [DataRow("cuda", "hy-mt2-18-q8-plain-beta", "DROPSPACE_CUDA_MODEL")]
    [DataRow("cpu", "hy-mt2-7b-q8-plain-beta", "DROPSPACE_CUDA_MODEL_7B")]
    [DataRow("vulkan", "hy-mt2-7b-q8-plain-beta", "DROPSPACE_CUDA_MODEL_7B")]
    [DataRow("cuda", "hy-mt2-7b-q8-plain-beta", "DROPSPACE_CUDA_MODEL_7B")]
    public async Task SamePinnedGgufColdWarmUsefulProgressResourcesAndCancellation(string mode, string modelId, string variable)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Real comparison needs a Windows x64 cloud host."); return; }
        var modelPath = Environment.GetEnvironmentVariable(variable);
        var shippingPath = Environment.GetEnvironmentVariable("DROPSPACE_CUDA_COMPARISON_RUNTIME");
        var cudaPath = Environment.GetEnvironmentVariable("DROPSPACE_CUDA_COMPONENT");
        if (modelPath is null || shippingPath is null || (mode == "cuda" && cudaPath is null))
        { Assert.Inconclusive("Explicit existing GGUF and reviewed runtime/component paths are required. No downloads."); return; }
        var model = AiLyricsModelCatalog.FindSelectable(modelId)!;
        // Retain the verified GGUF against concurrent modification for the entire native run.
        using var modelLease = File.OpenRead(modelPath);
        Assert.AreEqual(model.Bytes, modelLease.Length);
        Assert.AreEqual(model.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(modelLease)));
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-cuda-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cpu = new AiLyricsRuntimePackage(name => File.OpenRead(Path.Combine(shippingPath,
            name["DropSpace.AiLyricsRuntime.".Length..])), Path.Combine(root, "cpu"));
        var cuda = mode == "cuda" ? new CudaLyricsRuntimePackage(name => File.OpenRead(Path.Combine(cudaPath!,
            name[CudaLyricsRuntimePackage.ResourcePrefix.Length..])), Path.Combine(root, "cuda")) : null;
        using var runner = cuda is null ? new PersistentPlainLyricsRunner(cpu, new() { GpuEnabled = mode == "vulkan" }) :
            new PersistentPlainLyricsRunner(cpu, cuda, new());
        using var backend = cuda is null ? (IAiLyricsBackend)new PlainHyLyricsBackend(new(new(Path.Combine(root, "cache"))), runner, cpu, root) :
            new CudaPlainHyLyricsBackend(new(new(Path.Combine(root, "cache"))), runner, cpu, cuda, root);
        var source = new LyricsDocument([
            new(TimeSpan.Zero, TimeSpan.FromSeconds(4), "The morning light is on the window.", null, []),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), "We will meet beside the river.", null, []),
        ], LyricsProviderKind.LocalLrc);
        var cache = new AiLyricsCache(Path.Combine(root, "cache"));
        var identity = cuda is null ? PlainHyLyricsProtocol.InferenceIdentity(cpu.GetManifestCacheIdentity(), model.Sha256) :
            ((CudaPlainHyLyricsBackend)backend).Identity(model.Sha256);
        var package = new AiLyricsResolvedPackage(backend.Id, identity, modelPath, "unused-by-resident", null,
            CacheGeneration: cache.Generation, ModelId: model.Id, VerifiedModelSha256: model.Sha256);
        var records = new List<object>();
        var pids = new HashSet<int>();
        long peakWorkingSet = 0, peakPrivate = 0;
        using var sampling = new CancellationTokenSource();
        var sample = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                try
                {
                    if (runner.ActiveProcessId is { } pid)
                    {
                        lock (pids) pids.Add(pid);
                        using var process = Process.GetProcessById(pid);
                        process.Refresh();
                        peakWorkingSet = Math.Max(peakWorkingSet, process.PeakWorkingSet64);
                        peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                    }
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                await Task.Delay(10).ConfigureAwait(false);
            }
        });
        try
        {
            for (var run = 0; run < 2; run++)
            {
                var watch = Stopwatch.StartNew();
                double? firstUsefulMs = null;
                var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true, (update, _) =>
                {
                    if (LyricsTranslationOutput.HasUsefulLocalTranslation(update.Document, "zh-Hans"))
                        firstUsefulMs ??= watch.Elapsed.TotalMilliseconds;
                    return Task.CompletedTask;
                });
                // Different owned track IDs prevent a complete-song cache hit while retaining one resident.
                var query = new LyricsQuery("Native comparison " + run, "DropSpace", "", TimeSpan.FromSeconds(10), "cuda-compare:" + run);
                var result = await backend.TranslateAsync(package, query, source, "zh-Hans", default, progress);
                Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
                Assert.IsNotNull(firstUsefulMs);
                Assert.AreEqual(mode, runner.LastExecutionBackend, "GPU comparison cannot pass via CPU fallback.");
                Assert.IsFalse(runner.LastExecutionUsedCpuFallback);
                Assert.IsNotNull(runner.ActiveProcessId);
                lock (pids) pids.Add(runner.ActiveProcessId.Value);
                records.Add(new { state = run == 0 ? "process-cold-os-cache-uncontrolled" : "resident-warm-no-song-cache",
                    firstUsefulMs, fullSongMs = watch.Elapsed.TotalMilliseconds, pid = runner.ActiveProcessId,
                    deviceAdmissionSnapshot = runner.ActiveDevice,
                    output = result.Document.Lines.Select(x => x.Secondary).ToArray() });
            }
            lock (pids) Assert.HasCount(1, pids, "Warm measurement must reuse the same native owner.");
            using var cancel = new CancellationTokenSource();
            runner.TranslationRequestSent += cancel.Cancel;
            var cancellationWatch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunPlainAsync("unused", modelPath,
                PlainHyLyricsProtocol.BuildPrompt(source.Lines[0].Text, "zh-Hans"), root, cancel.Token, model.Sha256));
            await runner.DrainCleanupAsync(default);
            var cancellationDrainMs = cancellationWatch.Elapsed.TotalMilliseconds;
            await sampling.CancelAsync();
            await sample;
            foreach (var pid in pids)
            {
                try { using var process = Process.GetProcessById(pid); Assert.IsTrue(process.HasExited); }
                catch (ArgumentException) { }
            }
            Assert.AreEqual(1, LocalInferenceProcess.InferenceGate.CurrentCount);
            Assert.HasCount(0, Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories));
            TestContext.WriteLine(JsonSerializer.Serialize(new
            {
                status = "real-native-structural-evidence-semantic-review-required", mode, modelId, model.Sha256,
                engineCommit = AiLyricsRuntimePackage.SourceCommit, cpuManifest = cpu.GetManifestCacheIdentity(),
                cudaManifest = cuda?.GetManifestCacheIdentity(), PlainHyLyricsProtocol.Template, PlainHyLyricsProtocol.SamplerIdentity,
                records, peakWorkingSetBytes = peakWorkingSet, sampledPeakPrivateBytes = peakPrivate,
                devicePeakVramBytes = (long?)null, devicePeakVramNote = "Admission snapshot is not peak VRAM. Capture Nsight/nvidia-smi separately.",
                cancellationDrainMs, cleanupConfirmed = true,
            }));
        }
        finally
        {
            await sampling.CancelAsync();
            await sample;
            await runner.DrainCleanupAsync(default);
            Directory.Delete(root, true);
        }
    }
}
