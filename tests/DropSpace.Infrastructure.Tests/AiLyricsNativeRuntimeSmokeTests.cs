using System.Security.Cryptography;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AiLyricsNativeRuntimeSmokeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("NativeAiRuntime")]
    [DataRow("hy-mt2-18-q8-plain-beta", "DROPSPACE_AI_SMOKE_MODEL")]
    public async Task PinnedWindowsRuntimeTranslatesOriginalLinesAndHonorsCancellation(string modelId, string variable)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Shipping runtime smoke requires Windows x64."); return; }
        var descriptor = AiLyricsModelCatalog.Find(modelId)!;
        var model = Environment.GetEnvironmentVariable(variable);
        var runtime = Environment.GetEnvironmentVariable("DROPSPACE_AI_SMOKE_RUNTIME");
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(runtime))
        {
            Assert.Inconclusive("Set DROPSPACE_AI_SMOKE_MODEL and DROPSPACE_AI_SMOKE_RUNTIME for the native release gate.");
            return;
        }
        Assert.IsTrue(File.Exists(model), "The explicitly configured smoke model must exist.");
        Assert.AreEqual(descriptor.Bytes, new FileInfo(model).Length);
        await using (var input = File.OpenRead(model))
            Assert.AreEqual(descriptor.Sha256,
                Convert.ToHexStringLower(await SHA256.HashDataAsync(input)));
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-native-ai-smoke", Guid.NewGuid().ToString("N"));
        var phase = "runtime extraction";
        Exception? primaryFailure = null;
        try
        {
            var package = new AiLyricsRuntimePackage(name =>
            {
                var filename = name switch
                {
                    AiLyricsRuntimePackage.ManifestResourceName => "runtime-manifest.json",
                    AiLyricsRuntimePackage.ExecutableResourceName => "llama-completion.exe",
                    AiLyricsRuntimePackage.Avx2ExecutableResourceName => "llama-completion-avx2.exe",
                    "DropSpace.AiLyricsRuntime.llama-tokenize.exe" => "llama-tokenize.exe",
                    "DropSpace.AiLyricsRuntime.plain-lyrics-worker.exe" => "plain-lyrics-worker.exe",
                    "DropSpace.AiLyricsRuntime.plain-lyrics-worker-avx2.exe" => "plain-lyrics-worker-avx2.exe",
                    "DropSpace.AiLyricsRuntime.plain-lyrics-worker-vulkan.exe" => "plain-lyrics-worker-vulkan.exe",
                    _ => throw new InvalidOperationException("Unexpected test resource."),
                };
                return File.OpenRead(Path.Combine(runtime, filename));
            }, Path.Combine(root, "runtime"));
            var executable = await package.EnsureExecutableAsync(CancellationToken.None);
            using var runner = new PersistentPlainLyricsRunner(package, new AiLyricsRuntimeOptions { GpuEnabled = false });
            var source = new LyricsDocument([
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "The morning light is on the window.", null, []),
                new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), "We will meet beside the river.", null, []),
            ], LyricsProviderKind.LocalLrc);
            var query = new LyricsQuery("Native verification", "DropSpace", "", TimeSpan.FromSeconds(10));
            var staging = Path.Combine(root, "prompts");
            var identity = PlainHyLyricsProtocol.InferenceIdentity(package.GetManifestCacheIdentity());
            var cache = new AiLyricsCache(Path.Combine(root, "cache"));
            var coordinator = new PlainHyLyricsCoordinator(cache);
            using var backend = new PlainHyLyricsBackend(coordinator, runner, package, staging);
            var resolved = new AiLyricsResolvedPackage(PlainHyLyricsBackend.BackendId, identity, model, executable, null,
                CacheGeneration: cache.Generation);
            phase = "actual plaintext Beta backend translation and host mapping";
            var result = await backend.TranslateAsync(resolved, query, source, "zh-CN", CancellationToken.None);
            Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
            var translated = result.Document;
            for (var index = 0; index < source.Lines.Count; index++)
            {
                Assert.AreEqual(source.Lines[index].Text, translated.Lines[index].Text);
                Assert.AreEqual(source.Lines[index].Start, translated.Lines[index].Start);
                Assert.AreEqual(source.Lines[index].End, translated.Lines[index].End);
                Assert.AreEqual(LyricsTranslationOrigin.LocalAi, translated.Lines[index].TranslationOrigin);
                Assert.IsTrue(translated.Lines[index].Secondary!.Any(character => character is >= '\u4e00' and <= '\u9fff'));
            }
            await backend.DrainCleanupAsync(CancellationToken.None);
            Assert.IsFalse(Directory.Exists(staging) && Directory.GetFiles(staging).Length != 0);
            TestContext.WriteLine($"{modelId}: {PlainHyLyricsProtocol.Version}, {PlainHyLyricsProtocol.HostMappingVersion}, complete native plaintext responses mapped by host; no JSON grammar. Structural smoke is not semantic-quality approval.");
            TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(translated.Lines.Select((line, id) => new { id, line.Text, line.Secondary })));
            phase = "plaintext completion cancellation";
            var prompt = PlainHyLyricsProtocol.BuildPrompt(source.Lines[0].Text, "zh-CN");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunPlainAsync(executable, model, prompt, staging, cancel.Token, descriptor.Sha256));
            await runner.DrainCleanupAsync(CancellationToken.None);
            Assert.IsFalse(Directory.Exists(staging) && Directory.GetFiles(staging).Length != 0);
            phase = "runtime cleanup after successful translation and cancellation checks";
        }
        catch (Exception error)
        {
            primaryFailure = error;
            TestContext.WriteLine($"{modelId}: failed during {phase}: {error}");
            if (error.Data["LocalInferenceShutdownFailure"] is string shutdownFailure)
                TestContext.WriteLine($"{modelId}: additional shutdown failure: {shutdownFailure}");
            throw;
        }
        finally
        {
            PreservePrimaryFailureDuringCleanup(
                () => { if (Directory.Exists(root)) Directory.Delete(root, true); },
                primaryFailure,
                error => TestContext.WriteLine($"{modelId}: cleanup failed after {phase}: {error}"));
        }
    }

    internal static void PreservePrimaryFailureDuringCleanup(Action cleanup, Exception? primaryFailure, Action<Exception> report)
    {
        try { cleanup(); }
        catch (Exception error) when (primaryFailure is not null && error is IOException or UnauthorizedAccessException)
        {
            report(error);
        }
    }

    [TestMethod]
    public void CleanupFailureDoesNotReplaceTheNativeFailure()
    {
        var primary = new InvalidDataException("Native output failed validation.");
        var cleanup = new UnauthorizedAccessException("Runtime image is still open.");
        Exception? reported = null;
        var actual = Assert.ThrowsExactly<InvalidDataException>(() =>
        {
            try { throw primary; }
            finally { PreservePrimaryFailureDuringCleanup(() => throw cleanup, primary, error => reported = error); }
        });
        Assert.AreSame(primary, actual);
        Assert.AreSame(cleanup, reported);
    }

    [TestMethod]
    public void CleanupFailureStillFailsAnOtherwiseSuccessfulNativeRun()
    {
        var cleanup = new UnauthorizedAccessException("Runtime image is still open.");
        var actual = Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            PreservePrimaryFailureDuringCleanup(() => throw cleanup, null, _ => Assert.Fail("No failure should be suppressed.")));
        Assert.AreSame(cleanup, actual);
    }
}
