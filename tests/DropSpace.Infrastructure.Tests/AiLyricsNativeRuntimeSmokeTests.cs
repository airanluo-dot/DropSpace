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
    [DataRow("hy-mt2-standard", "DROPSPACE_AI_SMOKE_MODEL")]
    [DataRow("hy-mt2-lightweight", "DROPSPACE_AI_SMOKE_COMPACT_MODEL")]
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
                    _ => throw new InvalidOperationException("Unexpected test resource."),
                };
                return File.OpenRead(Path.Combine(runtime, filename));
            }, Path.Combine(root, "runtime"));
            var executable = await package.EnsureExecutableAsync(CancellationToken.None);
            using var runner = new LlamaCompletionRunner();
            var source = new LyricsDocument([
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), "The morning light is on the window.", null, []),
                new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), "We will meet beside the river.", null, []),
            ], LyricsProviderKind.LocalLrc);
            var query = new LyricsQuery("Native verification", "DropSpace", "", TimeSpan.FromSeconds(10));
            var prompt = LyricsTranslationPrompt.Build(query, source, [0, 1], "zh-CN");
            var staging = Path.Combine(root, "prompts");
            var tokenizer = await package.EnsureTokenizerAsync(CancellationToken.None);
            phase = "tokenization";
            var tokens = await runner.CountTokensAsync(tokenizer, model, prompt, staging, CancellationToken.None, descriptor.Sha256);
            Assert.IsTrue(tokens > 0 && tokens <= LyricsTranslationPrompt.MaximumPromptTokens);
            Assert.AreEqual(0, Directory.GetFiles(staging).Length);
            phase = "translation and output validation";
            var output = await runner.RunAsync(executable, model, prompt, staging, CancellationToken.None, descriptor.Sha256, [0, 1]);
            Assert.IsTrue(LyricsTranslationOutput.TryApply(output, source, [0, 1], "zh-CN", out var translated),
                "The native output must satisfy the strict line-ID protocol.");
            for (var index = 0; index < source.Lines.Count; index++)
            {
                Assert.AreEqual(source.Lines[index].Text, translated.Lines[index].Text);
                Assert.AreEqual(source.Lines[index].Start, translated.Lines[index].Start);
                Assert.AreEqual(source.Lines[index].End, translated.Lines[index].End);
                Assert.AreEqual(LyricsTranslationOrigin.LocalAi, translated.Lines[index].TranslationOrigin);
                Assert.IsTrue(translated.Lines[index].Secondary!.Any(character => character is >= '\u4e00' and <= '\u9fff'));
            }
            Assert.AreEqual(0, Directory.GetFiles(staging).Length);
            TestContext.WriteLine($"{modelId}: native translation and strict output validation passed.");
            phase = "tokenizer cancellation";
            using (var tokenizerCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    runner.CountTokensAsync(tokenizer, model, prompt, staging, tokenizerCancel.Token, descriptor.Sha256));
            Assert.AreEqual(0, Directory.GetFiles(staging).Length);
            phase = "completion cancellation";
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync(executable, model, prompt, staging, cancel.Token, descriptor.Sha256, [0, 1]));
            Assert.AreEqual(0, Directory.GetFiles(staging).Length);
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
