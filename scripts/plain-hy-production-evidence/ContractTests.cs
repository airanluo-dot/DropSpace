using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace PlainHyProductionEvidence;

/// <summary>Platform-neutral contract probes. These never claim native execution or emit release evidence.</summary>
internal static class ContractTests
{
    internal static async Task RunAsync()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "DropSpace-production-capture-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Program.Require(Program.ValidGpuRoute("cpu", true) && Program.ValidGpuRoute("vulkan", false) &&
                !Program.ValidGpuRoute("cpu", false) && !Program.ValidGpuRoute("vulkan", true) &&
                !Program.ValidGpuRoute(null, false), "GPU-default probe route/fallback consistency failed.");
            var probe = new GpuDefaultProbe(1, "production-gpu-default-probe", true, true, "cpu", true,
                "unverified", "en", 12, "原句", PlainHyLyricsProtocol.BuildPrompt("原句", "en"), "Original line",
                "Translated", 100, true, new(123, "2020-01-01T00:00:00Z", "plain-lyrics-worker.exe", "2020-01-01T00:00:01Z"),
                new string('a', 64), new("contract-model", new string('b', 64), 10), new string('c', 64), new string('d', 64), new string('e', 64), null);
            using (var document = JsonDocument.Parse(JsonSerializer.Serialize(probe, Program.Json)))
            {
                var root = document.RootElement;
                Program.Require(root.GetProperty("model").GetProperty("id").GetString() == "contract-model" &&
                    root.GetProperty("gpuEnabled").GetBoolean() && root.GetProperty("usedCpuFallback").GetBoolean() &&
                    root.GetProperty("deviceVendor").GetString() == "unverified" && root.GetProperty("fixtureLineId").GetInt32() == 12 &&
                    root.GetProperty("cleanupConfirmed").GetBoolean() && root.GetProperty("residentSourceSha256").GetString() == new string('e', 64),
                    "Supplemental GPU probe serialized shape is incomplete.");
            }
            var args = Program.NativeArguments();
            Program.Require(args[0] == "--model" && args[1] == "$MODEL" && args[2] == "--mode" && args[3] == "cpu", "Argument normalization failed.");
            Program.Require(!args.Contains("-j") && args.Length == 4, "Not the production plaintext profile.");
            var largeArgs = Program.NativeArguments(AiLyricsModelCatalog.ExperimentalLargePlain.Id);
            Program.Require(largeArgs.SequenceEqual(args.Concat(new[] { "--model-profile", "hy-mt2-7b-q8" })),
                "7B must select its own pinned resource profile without changing the sampler.");
            try { Program.NativeArguments("unrecognized"); throw new InvalidOperationException("Unknown model accepted."); }
            catch (InvalidDataException) { }
            foreach (var model in AiLyricsModelCatalog.All)
            {
                var config = JsonSerializer.Deserialize<CaptureConfiguration>(JsonSerializer.Serialize(new
                {
                    schemaVersion = 3, modelId = model.Id, modelSha256 = model.Sha256, modelBytes = model.Bytes,
                    promptProfile = "production-plain-hy", outputSchema = PlainHyLyricsProtocol.HostMappingVersion,
                    promptVersion = PlainHyLyricsProtocol.Version, backendId = PlainHyLyricsBackend.BackendId,
                    acceptanceVersion = PlainHyLyricsProtocol.AcceptanceVersion, samplerIdentity = PlainHyLyricsProtocol.SamplerIdentity,
                    captureMethod = Program.CaptureMethod, loadOnly = false, runtimeVariant = "baseline", gpuEnabled = false,
                    executionLimits = new ExecutionLimits(PlainHyLyricsProtocol.WholeSongSeconds, 60, model == AiLyricsModelCatalog.ExperimentalLargePlain ? 12288 : 3072, 1800, 16384),
                    nativeArguments = Program.NativeArguments(model.Id), logicalEvidenceRoot = "scripts/ai-model-qa/evidence/contract-only",
                }, Program.Json), Program.Json)!;
                Program.ValidateConfiguration(config);
                foreach (var invalid in new[] { config with { SchemaVersion = 2 }, config with { ModelSha256 = new string('0', 64) },
                    config with { ExecutionLimits = config.ExecutionLimits with { WholeSongSeconds = 300 } },
                    config with { ExecutionLimits = config.ExecutionLimits with { MemoryMiB = config.ExecutionLimits.MemoryMiB - 1 } } })
                {
                    var rejectedConfig = false;
                    try { Program.ValidateConfiguration(invalid); } catch (InvalidDataException) { rejectedConfig = true; }
                    Program.Require(rejectedConfig, "Model configuration schema, pinned hash and resource budget must be exact.");
                }
            }
            var admissionSource = Source("I love you", "作词：Someone", "君の名前を忘れるとは約束していない。",
                "作曲：Other\nI need you\n编曲：Other\n君の名前を忘れるとは約束していない。");
            var admission = Program.Admission(admissionSource, "en");
            Program.Require(admission[0].Reason == "same-target-language" && admission[1].Reason == "credit" &&
                admission[2].Eligible && admission[3].Eligible && admission[3].Segments.Length == 1 &&
                admission[3].Segments[0].Text == admissionSource.Lines[2].Text,
                "Admission must retain original IDs, exclude credits/same target segments and keep foreign segments.");
            var multipleDirectory = Path.Combine(temporary, "multiple");
            Directory.CreateDirectory(multipleDirectory);
            var multipleSource = Source("作词：Someone\nI love you\n编曲：Other\nI need you");
            using var multiple = new ObservedRunner(new FakeRunner((_, i, _) => Task.FromResult(i == 1 ? "我爱你" : "我需要你")), multipleDirectory);
            multiple.Begin("contract-segments", "zh-Hans", multipleSource);
            var multipleProgress = new List<int>();
            var multipleCache = new AiLyricsCache(Path.Combine(multipleDirectory, "cache"));
            var multipleCoordinator = new PlainHyLyricsCoordinator(multipleCache);
            var multipleQuery = new LyricsQuery("test", "", "", TimeSpan.Zero);
            var multipleResult = await multipleCoordinator.TranslateAsync(multipleQuery, multipleSource, "zh-Hans", new string('a', 64), multipleCache.Generation,
                (prompt, token) => multiple.RunPlainAsync("unused", "unused", prompt, multipleDirectory, token, AiLyricsModelCatalog.ExperimentalPlain.Sha256), default,
                new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true, (update, _) => { multipleProgress.Add(update.LineId); return Task.CompletedTask; }, "segments")).ConfigureAwait(false);
            Program.Require(multiple.Calls.Select(call => call.LineId).SequenceEqual(new[] { 0, 0 }) &&
                multiple.Calls.Select(call => call.SegmentIndex).SequenceEqual(new[] { 0, 1 }) && multipleProgress.SequenceEqual(new[] { 0 }) &&
                multipleResult.Document.Lines[0].Text == multipleSource.Lines[0].Text && multipleResult.Document.Lines[0].Secondary == "我爱你 我需要你",
                "Segment calls/progress/final document must preserve the original single display row.");
            var multipleCached = await multipleCoordinator.TryGetCachedAsync(multipleQuery, multipleSource, "zh-Hans", new string('a', 64), default).ConfigureAwait(false);
            Program.Require(multipleCached is not null && Program.SameResult(multipleCached, multipleResult), "Segment cache changed stable row mapping.");
            var source = Source("原句", "Already English");
            var cache = new AiLyricsCache(Path.Combine(temporary, "cache"));
            var coordinator = new PlainHyLyricsCoordinator(cache);
            var identity = PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64));
            var query = new LyricsQuery("Never send this metadata", "artist", "album", TimeSpan.FromSeconds(2), "test-track");
            var fake = new FakeRunner((_, index, _) => Task.FromResult(index == 1 ? "Original line" : "Already English"));
            using var observer = new ObservedRunner(fake, temporary);
            observer.Begin("contract", "en", source);
            var progressIds = new List<int>();
            var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true, (update, token) =>
            {
                token.ThrowIfCancellationRequested();
                Program.Require(update.IsCurrent && update.IsEphemeral && update.CompletedLineCount == progressIds.Count + 1,
                    "Progress callback is not a current sequential ephemeral update.");
                progressIds.Add(update.LineId);
                return Task.CompletedTask;
            }, "contract-progress");
            var result = await coordinator.TranslateAsync(query, source, "en", identity, cache.Generation,
                (prompt, token) => observer.RunPlainAsync("unused-executable", "unused-model", prompt, temporary, token,
                    AiLyricsModelCatalog.ExperimentalPlain.Sha256), default, progress).ConfigureAwait(false);
            Program.Require(progressIds.SequenceEqual(new[] { 0, 1 }), "Fixed-zero playback position changed source-line order.");
            Program.Require(result.Outcome == LyricsTranslationOutcome.Translated && observer.Calls.Count == 2, "Complete production coordinator path failed.");
            Program.Require(observer.Calls[0].LineId == 0 && observer.Calls[1].LineId == 1 && observer.Calls[1].Output == "Already English", "Host mapping lost a neutral unchanged output.");
            Program.Require(observer.Calls.All(x => !x.Prompt.Contains(query.Title, StringComparison.Ordinal)), "Metadata leaked into model prompt.");
            using (var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temporary, "call-002.json"))))
                Program.Require(record.RootElement.GetProperty("output").GetString() == "Already English", "Runner return value was not preserved.");
            var cached = await coordinator.TranslateAsync(query, source, "en", identity, cache.Generation,
                (_, _) => throw new InvalidOperationException("Unexpected inference on cache hit."), default).ConfigureAwait(false);
            Program.Require(Program.SameResult(cached, result), "Cache result differs from cold result.");

            var copies = Source("Tokyo", "Already English");
            var noUsefulCalls = 0;
            Task<string> Copy(string prompt, CancellationToken _)
            {
                noUsefulCalls++;
                return Task.FromResult(prompt[(prompt.IndexOf('\n') + 1)..]);
            }
            var noUseful = await coordinator.TranslateAsync(query, copies, "en", identity, cache.Generation, Copy, default).ConfigureAwait(false);
            await coordinator.TranslateAsync(query, copies, "en", identity, cache.Generation, Copy, default).ConfigureAwait(false);
            Program.Require(noUseful.Outcome == LyricsTranslationOutcome.NoUsefulTranslation && noUsefulCalls == 2,
                "No-useful result did not memoize without new inference.");

            var failures = Path.Combine(temporary, "failure");
            Directory.CreateDirectory(failures);
            using var failing = new ObservedRunner(new FakeRunner((_, _, _) => throw new IOException("contract failure")), failures);
            failing.Begin("contract-failure", "zh-Hans", source);
            try
            {
                await failing.RunPlainAsync("unused", "unused", PlainHyLyricsProtocol.BuildPrompt(source.Lines[0].Text, "zh-Hans"),
                    failures, default, AiLyricsModelCatalog.ExperimentalPlain.Sha256).ConfigureAwait(false);
                throw new InvalidDataException("Runner failure was swallowed.");
            }
            catch (IOException) { }
            Program.Require(failing.Calls.Single().Status == "failed" && failing.Calls.Single().Output is null,
                "Failed call must never fabricate returned output.");
            var verbatimDirectory = Path.Combine(temporary, "verbatim");
            Directory.CreateDirectory(verbatimDirectory);
            const string verbatim = "\n unexpected\nmultiline \t";
            using var verbatimObserver = new ObservedRunner(new FakeRunner((_, _, _) => Task.FromResult(verbatim)), verbatimDirectory);
            verbatimObserver.Begin("contract-verbatim", "en", source);
            var returned = await verbatimObserver.RunPlainAsync("unused", "unused", PlainHyLyricsProtocol.BuildPrompt(source.Lines[0].Text, "en"),
                verbatimDirectory, default, AiLyricsModelCatalog.ExperimentalPlain.Sha256).ConfigureAwait(false);
            Program.Require(returned == verbatim && verbatimObserver.Calls.Single().Output == verbatim, "Observer modified runner-returned text.");
            try
            {
                Program.WriteNew(Path.Combine(temporary, "call-002.json"), new { replacement = true });
                throw new InvalidDataException("Create-only output was overwritten.");
            }
            catch (IOException) { }
            var mappingDirectory = Path.Combine(temporary, "mapping");
            Directory.CreateDirectory(mappingDirectory);
            using var mappingObserver = new ObservedRunner(new FakeRunner((_, _, _) => Task.FromResult("unused")), mappingDirectory);
            mappingObserver.Begin("contract-mapping", "en", source);
            var rejected = false;
            try
            {
                await mappingObserver.RunPlainAsync("unused", "unused", "wrong prompt", mappingDirectory, default,
                    AiLyricsModelCatalog.ExperimentalPlain.Sha256).ConfigureAwait(false);
            }
            catch (InvalidDataException) { rejected = true; }
            Program.Require(rejected && mappingObserver.TotalCalls == 0, "Mismatched prompt must fail before inference.");
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static LyricsDocument Source(params string[] lines) => new(lines.Select((text, i) =>
        new LyricsLine(TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), text, null, [])).ToArray(), LyricsProviderKind.LocalLrc);

    private sealed class FakeRunner(Func<string, int, CancellationToken, Task<string>> run) : IPlainLyricsRunner
    {
        private int _calls;
        public Task<string> RunPlainAsync(string executablePath, string modelPath, string prompt, string stagingDirectory,
            CancellationToken cancellationToken, string verifiedModelSha256) => run(prompt, ++_calls, cancellationToken);
        public Task DrainCleanupAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
}
