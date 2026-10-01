using System.Security.Cryptography;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AiLyricsNativeRuntimeSmokeTests
{
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
        try
        {
            var package = new AiLyricsRuntimePackage(name =>
            {
                var filename = name switch
                {
                    AiLyricsRuntimePackage.ManifestResourceName => "runtime-manifest.json",
                    AiLyricsRuntimePackage.ExecutableResourceName => "llama-completion.exe",
                    AiLyricsRuntimePackage.Avx2ExecutableResourceName => "llama-completion-avx2.exe",
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
            var output = await runner.RunAsync(executable, model, prompt, staging, CancellationToken.None, descriptor.Sha256);
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
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync(executable, model, prompt, staging, cancel.Token, descriptor.Sha256));
            Assert.AreEqual(0, Directory.GetFiles(staging).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
