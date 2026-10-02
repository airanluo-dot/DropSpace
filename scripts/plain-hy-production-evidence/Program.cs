using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

namespace PlainHyProductionEvidence;

internal static class Program
{
    internal const string CaptureMethod = "PlainHyLyricsBackend+PlainHyLyricsCoordinator+PersistentPlainLyricsRunner.RunPlainAsync";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] Targets = ["en", "zh-Hans"];

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--print-native-arguments"])
        {
            Console.WriteLine(JsonSerializer.Serialize(NativeArguments(), Json));
            return 0;
        }
        if (args is ["--contract-self-test"])
        {
            await ContractTests.RunAsync().ConfigureAwait(false);
            Console.WriteLine("Production capture contract tests passed (fake inference; no native evidence emitted).");
            return 0;
        }
        if (args is not ["--capture", var configPath])
        {
            Console.Error.WriteLine("Use --contract-self-test or --capture <configuration.json> through Run-WindowsProductionEvidence.ps1.");
            return 64;
        }
        CaptureConfiguration? config = null;
        try
        {
            config = JsonSerializer.Deserialize<CaptureConfiguration>(File.ReadAllText(configPath), Json)
                ?? throw new InvalidDataException("Missing capture configuration.");
            await CaptureAsync(config, configPath).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (config is not null && Directory.Exists(config.OutputDirectory))
                WriteNew(Path.Combine(config.OutputDirectory, "harness-failure.json"), new
                { status = "failed-or-incomplete", error = error.ToString(), nativeEvidenceEmitted = false });
            return 1;
        }
    }

    private static async Task CaptureAsync(CaptureConfiguration config, string configPath)
    {
        Require(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "Native capture requires Windows x64.");
        ValidateConfiguration(config);
        using var scope = JsonDocument.Parse(File.ReadAllBytes(config.ScopePath));
        ValidateScope(config, scope.RootElement);
        var source = JsonSerializer.Deserialize<LyricsDocument>(File.ReadAllText(config.SourcePath), Json)
            ?? throw new InvalidDataException("Invalid source fixture.");
        Require(source.Lines.Count == 48 && source.Lines.All(x => !string.IsNullOrWhiteSpace(x.Text)),
            "The complete 48-line fixture is required.");
        var configHash = HashFile(configPath);
        using var inputLocks = LockInputs(config, configPath, scope.RootElement);
        var before = VerifyInputs(config, scope.RootElement);
        WriteNew(Path.Combine(config.OutputDirectory, "harness-inputs-before.json"), before);
        var runtime = new AiLyricsRuntimePackage(Assembly.GetExecutingAssembly(), Path.Combine(config.OutputDirectory, "runtime-cache"), config.RuntimeVariant == "avx2");
        Require(runtime.GetManifestCacheIdentity() == config.RuntimeManifestSha256,
            "The manifest embedded at compilation differs from the execution snapshot.");
        var extractedExecutable = await runtime.EnsureResidentWorkerAsync(gpu: false, default).ConfigureAwait(false);
        Require(Identity(extractedExecutable) == new FileIdentity(config.ExecutableSha256, config.ExecutableBytes),
            "Production extraction selected a different resident worker than the explicit capture variant.");
        using var extractedRuntimeLock = new InputReadLocks([extractedExecutable]);
        WriteNew(Path.Combine(config.OutputDirectory, "runtime-extraction.json"), new
        { executable = extractedExecutable, sha256 = config.ExecutableSha256, bytes = config.ExecutableBytes, gpuEnabled = false });
        var identity = PlainHyLyricsProtocol.InferenceIdentity(runtime.GetManifestCacheIdentity());
        var cache = new AiLyricsCache(Path.Combine(config.OutputDirectory, "song-cache"));
        var staging = Path.Combine(config.OutputDirectory, "staging");
        Directory.CreateDirectory(staging);
        using var observer = new ObservedRunner(new PersistentPlainLyricsRunner(runtime, new AiLyricsRuntimeOptions { GpuEnabled = false }), config.OutputDirectory);
        using var backend = new PlainHyLyricsBackend(new PlainHyLyricsCoordinator(cache), observer, runtime, staging);
        var package = new AiLyricsResolvedPackage(PlainHyLyricsBackend.BackendId, identity, config.ModelPath,
            extractedExecutable, null, CacheGeneration: cache.Generation);
        var query = Query("source48");
        var cold = new List<SongCheck>();
        var repeats = new List<CacheCheck>();
        var technicalPassed = true;
        foreach (var target in Targets)
        {
            observer.Begin("cold", target, source);
            var watch = Stopwatch.StartNew();
            LyricsTranslationResult? result = null;
            string? failure = null;
            var progressEvents = new List<ProgressObservation>();
            var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
                (update, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Require(update.IsCurrent && update.IsEphemeral && update.CacheGeneration == cache.Generation &&
                        update.LineId == progressEvents.Count && update.CompletedLineCount == progressEvents.Count + 1 &&
                        update.TotalLineCount == source.Lines.Count, "Production progress context/mapping differs from the controlled source-order capture.");
                    progressEvents.Add(new(update.RequestIdentity, update.CacheGeneration, update.LineId,
                        update.CompletedLineCount, update.TotalLineCount, update.IsEphemeral, update.IsCurrent,
                        watch.Elapsed.TotalMilliseconds));
                    return Task.CompletedTask;
                }, "production-evidence-" + target);
            try { result = await backend.TranslateAsync(package, query, source, target, default, progress).ConfigureAwait(false); }
            catch (Exception error) { failure = error.ToString(); }
            watch.Stop();
            // Each call was already journaled. A failed/incomplete song is preserved and never retried.
            var calls = observer.Calls.ToArray();
            var residentReuse = calls.Length > 0 && calls.All(x => x.NativeProcess is not null) &&
                calls.Select(x => (x.NativeProcess!.Id, x.NativeProcess.StartedAt)).Distinct().Count() == 1;
            var complete = failure is null && result?.Outcome is LyricsTranslationOutcome.Translated or LyricsTranslationOutcome.NoUsefulTranslation
                && calls.Length == source.Lines.Count && calls.All(x => x.Status == "returned") && progressEvents.Count == source.Lines.Count && residentReuse;
            WriteNew(Path.Combine(config.OutputDirectory, target + ".runner-output.json"), new
            {
                schemaVersion = 1, kind = "production-runner-output", targetLanguage = target,
                outcome = result?.Outcome.ToString(), complete, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                error = failure, calls, progressEvents, playbackPositionSeconds = 0, residentProcessReuseConfirmed = residentReuse,
                firstProgressElapsedMilliseconds = progressEvents.FirstOrDefault()?.ElapsedMilliseconds,
                outputSemantics = "Exact PersistentPlainLyricsRunner.RunPlainAsync return values; not raw native stdout or JSON frames. The production runner removes its terminal marker and trims terminal whitespace."
            });
            cold.Add(new(target, result?.Outcome.ToString(), complete, calls.Length, watch.Elapsed.TotalMilliseconds, failure));
            await DrainBoundedAsync(backend).ConfigureAwait(false);
            Require(!Directory.EnumerateFiles(staging).Any(), "Production runner left a staging prompt after confirmed cleanup.");
            technicalPassed &= complete;
            if (!complete) continue;
            var count = observer.TotalCalls;
            observer.Begin("cache", target, source);
            watch.Restart();
            var cached = await backend.TryGetCachedResultAsync(config.ModelId, query, source, target, default).ConfigureAwait(false);
            var repeated = await backend.TranslateAsync(package, query, source, target, default).ConfigureAwait(false);
            watch.Stop();
            var additional = observer.TotalCalls - count;
            var matching = cached is not null && result is not null && SameResult(cached, result) && SameResult(repeated, result);
            repeats.Add(new(target, additional, matching, repeated.Outcome.ToString(), watch.Elapsed.TotalMilliseconds));
            technicalPassed &= matching && additional == 0;
        }
        WriteNew(Path.Combine(config.OutputDirectory, "song-checks.json"), new { cold, cache = repeats });
        // A distinct query/cache avoids accidentally exercising a cache hit during cancellation.
        var cancelSource = source with { Lines = [source.Lines[12]] };
        observer.Begin("cancellation", "en", cancelSource);
        var cancellation = await CheckCancellationAsync(backend, package, observer, cancelSource, extractedExecutable, staging).ConfigureAwait(false);
        WriteNew(Path.Combine(config.OutputDirectory, "cancellation-check.json"), cancellation);
        technicalPassed &= cancellation.CancellationObserved && cancellation.CleanupConfirmed;
        Require(cancellation.CleanupConfirmed, "Unconfirmed CPU cancellation cleanup blocks the default-setting GPU probe.");
        await DrainBoundedAsync(backend).ConfigureAwait(false);
        var gpuProbe = await CheckGpuDefaultAsync(config, runtime, source, identity).ConfigureAwait(false);
        WriteNew(Path.Combine(config.OutputDirectory, "gpu-default-probe.json"), gpuProbe);
        technicalPassed &= gpuProbe.Complete && gpuProbe.CleanupConfirmed;
        var after = VerifyInputs(config, scope.RootElement);
        WriteNew(Path.Combine(config.OutputDirectory, "harness-inputs-after.json"), after);
        var sourceUnchanged = before.SourceFingerprintSha256 == after.SourceFingerprintSha256;
        var modelUnchanged = before.Model == after.Model;
        var runtimeUnchanged = JsonSerializer.Serialize(before.Runtime, Json) == JsonSerializer.Serialize(after.Runtime, Json);
        var configurationUnchanged = HashFile(configPath) == configHash;
        technicalPassed &= sourceUnchanged && modelUnchanged && runtimeUnchanged && configurationUnchanged;
        WriteNew(Path.Combine(config.OutputDirectory, "technical-results.json"), new
        {
            schemaVersion = 1, status = technicalPassed ? "complete" : "failed-or-incomplete",
            coldTargets = cold.Where(x => x.Complete).Select(x => x.TargetLanguage).ToArray(),
            cacheTargets = repeats.Where(x => x.MatchesCold && x.AdditionalInferenceCalls == 0).Select(x => x.TargetLanguage).ToArray(),
            cacheAdditionalInferenceCalls = repeats.Sum(x => x.AdditionalInferenceCalls),
            cancellationObserved = cancellation.CancellationObserved, cleanupConfirmed = cancellation.CleanupConfirmed,
            sourceIdentityUnchanged = sourceUnchanged, modelIdentityUnchanged = modelUnchanged,
            runtimeIdentityUnchanged = runtimeUnchanged, configurationUnchanged,
            gpuDefaultProbeComplete = gpuProbe.Complete, gpuDefaultProbeCleanupConfirmed = gpuProbe.CleanupConfirmed,
            captureMethod = CaptureMethod, backend = nameof(PlainHyLyricsBackend), semanticStatus = "not-evaluated"
        });
        Require(technicalPassed, "Capture did not complete all required technical checks. Preserved failures; no successful native-output envelope is permitted.");
    }

    private static async Task<GpuDefaultProbe> CheckGpuDefaultAsync(CaptureConfiguration config,
        AiLyricsRuntimePackage runtime, LyricsDocument fixture, string identity)
    {
        const int fixtureLineId = 12;
        const string target = "en";
        var directory = Path.Combine(config.OutputDirectory, "gpu-default");
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, "staging");
        Directory.CreateDirectory(staging);
        var source = fixture with { Lines = [fixture.Lines[fixtureLineId]] };
        var cache = new AiLyricsCache(Path.Combine(directory, "cache"));
        var cpuPath = await runtime.EnsureResidentWorkerAsync(gpu: false, default).ConfigureAwait(false);
        var gpuPath = await runtime.EnsureResidentWorkerAsync(gpu: true, default).ConfigureAwait(false);
        using var gpuLock = new InputReadLocks([gpuPath]);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(config.RuntimeDirectory, "runtime-manifest.json")));
        var gpuMetadata = manifest.RootElement.GetProperty("resident").GetProperty("vulkan");
        Require(Identity(gpuPath) == new FileIdentity(gpuMetadata.GetProperty("sha256").GetString()!, gpuMetadata.GetProperty("bytes").GetInt64()),
            "Production GPU worker extraction differs from the immutable runtime snapshot.");
        using var runner = new PersistentPlainLyricsRunner(runtime, new AiLyricsRuntimeOptions { GpuEnabled = true });
        using var observer = new ObservedRunner(runner, directory);
        using var backend = new PlainHyLyricsBackend(new PlainHyLyricsCoordinator(cache), observer, runtime, staging);
        var package = new AiLyricsResolvedPackage(PlainHyLyricsBackend.BackendId, identity, config.ModelPath,
            cpuPath, null, CacheGeneration: cache.Generation);
        observer.Begin("gpu-default", target, source);
        var watch = Stopwatch.StartNew();
        LyricsTranslationResult? result = null;
        string? failure = null;
        NativeObservation? observed = null;
        string? actualBackend = null;
        var fallback = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(config.ExecutionLimits.PerLineSeconds));
        try
        {
            result = await backend.TranslateAsync(package, Query("gpu-default"), source, target, deadline.Token).ConfigureAwait(false);
            actualBackend = runner.LastExecutionBackend;
            fallback = runner.LastExecutionUsedCpuFallback;
            if (actualBackend is "cpu" or "vulkan")
                observed = ObserveProcesses(actualBackend == "vulkan" ? gpuPath : cpuPath).SingleOrDefault();
        }
        catch (Exception error) { failure = error.ToString(); }
        var drained = false;
        try { await DrainBoundedAsync(backend).ConfigureAwait(false); drained = true; }
        catch (Exception error) { failure = (failure is null ? "" : failure + "\n") + error; }
        watch.Stop();
        var nativeAbsent = !ObserveProcesses(cpuPath).Any() && !ObserveProcesses(gpuPath).Any();
        var cleanupConfirmed = drained && nativeAbsent && !Directory.EnumerateFiles(staging).Any();
        var call = observer.Calls.SingleOrDefault();
        var complete = failure is null && result?.Outcome is LyricsTranslationOutcome.Translated or LyricsTranslationOutcome.NoUsefulTranslation
            && observer.Calls.Count == 1 && call?.Status == "returned" && call.Output is not null && observed is not null &&
            ValidGpuRoute(actualBackend, fallback) && cleanupConfirmed;
        return new(1, "production-gpu-default-probe", true, complete, actualBackend, fallback, "unverified",
            target, fixtureLineId, source.Lines[0].Text, PlainHyLyricsProtocol.BuildPrompt(source.Lines[0].Text, target),
            call?.Output, result?.Outcome.ToString(), watch.Elapsed.TotalMilliseconds, cleanupConfirmed,
            observed, config.RuntimeManifestSha256, new(config.ModelId, config.ModelSha256, config.ModelBytes), config.FixtureSha256,
            config.SourceFingerprintSha256, manifest.RootElement.GetProperty("resident").GetProperty("sourceSha256").GetString()!, failure);
    }

    internal static bool ValidGpuRoute(string? actualBackend, bool usedCpuFallback) =>
        actualBackend == "vulkan" && !usedCpuFallback || actualBackend == "cpu" && usedCpuFallback;

    private static async Task DrainBoundedAsync(PlainHyLyricsBackend backend)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await backend.DrainCleanupAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    private static async Task<CancellationCheck> CheckCancellationAsync(PlainHyLyricsBackend backend,
        AiLyricsResolvedPackage package, ObservedRunner observer, LyricsDocument source, string executable, string staging)
    {
        await DrainBoundedAsync(backend).ConfigureAwait(false);
        var previous = ObserveProcesses(executable).Select(x => x.Id).ToHashSet();
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var task = backend.TranslateAsync(package, Query("cancellation"), source, "en", cancel.Token);
        NativeObservation? observed = null;
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            observed = ObserveProcesses(executable).FirstOrDefault(x => !previous.Contains(x.Id));
            if (observed is not null) break;
            await Task.Delay(10).ConfigureAwait(false);
        }
        var cancelAt = UtcNow();
        cancel.Cancel();
        var caughtCancellation = false;
        string? errorText = null;
        try { await task.WaitAsync(TimeSpan.FromSeconds(85)).ConfigureAwait(false); }
        catch (OperationCanceledException) { caughtCancellation = true; }
        catch (Exception error) { errorText = error.ToString(); }
        var drained = false;
        try { await DrainBoundedAsync(backend).ConfigureAwait(false); drained = true; }
        catch (Exception error) { errorText = (errorText is null ? "" : errorText + "\n") + error; }
        var stillAlive = observed is not null && ObserveProcesses(executable).Any(x => x.Id == observed.Id && x.StartedAt == observed.StartedAt);
        var promptsRemoved = !Directory.EnumerateFiles(staging).Any();
        var cancellationObserved = observed is not null && caughtCancellation && observer.Calls.Count == 1 && observer.Calls[0].Status == "cancelled";
        return new(cancellationObserved, drained && observed is not null && !stillAlive && promptsRemoved,
            observed, cancelAt, caughtCancellation, drained, stillAlive, promptsRemoved, watch.Elapsed.TotalMilliseconds, errorText);
    }

    internal static List<NativeObservation> ObserveProcesses(string executable)
    {
        var observations = new List<NativeObservation>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        observations.Add(new(process.Id, process.StartTime.ToUniversalTime().ToString("O"), executable, UtcNow()));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                { /* Unobservable processes never count as proof. */ }
            }
        }
        return observations;
    }

    internal static void ValidateConfiguration(CaptureConfiguration config)
    {
        var model = AiLyricsModelCatalog.ExperimentalPlain;
        Require(config.SchemaVersion == 2 && config.ModelId == model.Id && config.ModelSha256 == model.Sha256 && config.ModelBytes == model.Bytes,
            "Configuration model differs from the current production descriptor.");
        Require(config.PromptProfile == "production-plain-hy" && config.OutputSchema == PlainHyLyricsProtocol.HostMappingVersion &&
            config.PromptVersion == PlainHyLyricsProtocol.Version && config.BackendId == PlainHyLyricsBackend.BackendId &&
            config.AcceptanceVersion == PlainHyLyricsProtocol.AcceptanceVersion && config.SamplerIdentity == PlainHyLyricsProtocol.SamplerIdentity &&
            config.CaptureMethod == CaptureMethod && !config.LoadOnly, "Configuration does not select the production plain Hy profile.");
        Require(config.ExecutionLimits == new ExecutionLimits(300, 60, 3072, 1800, 16384), "Production execution limits differ.");
        Require(config.RuntimeVariant is "baseline" or "avx2" && !config.GpuEnabled, "An explicit CPU baseline/avx2 runtime with GPU disabled is required.");
        Require(config.NativeArguments.SequenceEqual(NativeArguments()), "Native arguments differ from production PersistentPlainLyricsRunner.BuildArguments.");
        Require(config.LogicalEvidenceRoot.StartsWith("scripts/ai-model-qa/evidence/", StringComparison.Ordinal) &&
            config.LogicalEvidenceRoot.Split('/').All(x => x.Length > 0 && x != "." && x != ".." && x.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')),
            "Invalid logical evidence path.");
        if (config.RuntimeVariant == "avx2") Require(Avx2.IsSupported && Fma.IsSupported && X86Base.IsSupported &&
            (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0, "AVX2/FMA/F16C CPU eligibility failed.");
    }

    private static void ValidateScope(CaptureConfiguration config, JsonElement scope)
    {
        foreach (var key in new[] { "promptProfile", "outputSchema", "promptVersion", "backendId", "acceptanceVersion", "samplerIdentity", "captureMethod" })
        {
            var serializedConfig = JsonSerializer.SerializeToElement(config, Json);
            Require(scope.GetProperty(key).GetString() == serializedConfig.GetProperty(key).GetString(), "Invocation scope mismatch: " + key);
        }
        Require(JsonSerializer.Deserialize<ExecutionLimits>(scope.GetProperty("executionLimits"), Json) == config.ExecutionLimits,
            "Scope execution limits differ.");
        Require(scope.GetProperty("nativeArguments").EnumerateArray().Select(x => x.GetString()).SequenceEqual(config.NativeArguments),
            "Scope native arguments differ.");
        Require(scope.GetProperty("samplerArguments").EnumerateArray().Select(x => x.GetString()).SequenceEqual(config.SamplerArguments),
            "Scope resident sampler arguments differ.");
        var models = scope.GetProperty("shippingModels").EnumerateArray().ToArray();
        Require(models.Length == 1 && models[0].GetProperty("id").GetString() == config.ModelId &&
            models[0].GetProperty("sha256").GetString() == config.ModelSha256 && models[0].GetProperty("bytes").GetInt64() == config.ModelBytes,
            "The captured model is not the sole scoped shipping model.");
        Require(scope.GetProperty("sources").GetProperty("sha256").GetString() == config.SourceFingerprintSha256 &&
            scope.GetProperty("fixture").GetProperty("sha256").GetString() == config.FixtureSha256, "Scope input identities differ.");
    }

    private static InputSnapshot VerifyInputs(CaptureConfiguration config, JsonElement scope)
    {
        Require(HashFile(config.SourcePath) == config.FixtureSha256, "Fixture snapshot changed.");
        var sourceHashes = new List<SourceIdentity>();
        foreach (var file in scope.GetProperty("sources").GetProperty("files").EnumerateArray())
        {
            var path = file.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing source path.");
            Require(!Path.IsPathRooted(path) && !path.Contains('\\') && path.Split('/').All(x => x.Length > 0 && x != "." && x != ".."), "Invalid source path.");
            var hash = HashText(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(config.RepositoryRoot, path))).Replace("\r\n", "\n", StringComparison.Ordinal));
            Require(hash == file.GetProperty("sha256").GetString(), "Production source changed: " + path);
            Require(HashText(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(config.OutputDirectory, "source-snapshot", path))).Replace("\r\n", "\n", StringComparison.Ordinal)) == hash,
                "Source snapshot differs: " + path);
            sourceHashes.Add(new(path, hash));
        }
        var compact = new JsonSerializerOptions(Json) { WriteIndented = false };
        var fingerprint = HashText(JsonSerializer.Serialize(sourceHashes, compact));
        Require(fingerprint == config.SourceFingerprintSha256, "Source fingerprint differs.");
        var model = Identity(config.ModelPath);
        Require(model == new FileIdentity(config.ModelSha256, config.ModelBytes), "Model integrity failed.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(config.RuntimeDirectory, "runtime-manifest.json")));
        var root = manifest.RootElement;
        Require(HashFile(Path.Combine(config.RuntimeDirectory, "runtime-manifest.json")) == config.RuntimeManifestSha256 &&
            root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("runtimeId").GetString() == AiLyricsRuntimePackage.RuntimeId &&
            root.GetProperty("sourceCommit").GetString() == AiLyricsRuntimePackage.SourceCommit, "Runtime manifest integrity failed.");
        var runtime = new List<RuntimeIdentity>();
        foreach (var name in new[] { "LICENSE-llama.cpp", "llama-completion.exe", "llama-completion-avx2.exe", "llama-tokenize.exe", "plain-lyrics-worker.exe", "plain-lyrics-worker-avx2.exe", "plain-lyrics-worker-vulkan.exe", "runtime-manifest.json" })
        {
            var identity = Identity(Path.Combine(config.RuntimeDirectory, name));
            Require(identity.Bytes > 0, "Runtime file is empty: " + name);
            runtime.Add(new(name, identity.Sha256, identity.Bytes));
        }
        Require(Directory.EnumerateFileSystemEntries(config.RuntimeDirectory).Count() == runtime.Count,
            "Runtime directory must contain exactly the complete eight-file shipping payload.");
        foreach (var (key, name) in new[] { ("", "llama-completion.exe"), ("avx2", "llama-completion-avx2.exe"), ("tokenizer", "llama-tokenize.exe") })
        {
            var item = key.Length == 0 ? root : root.GetProperty(key);
            var actual = runtime.Single(x => x.Path == name);
            Require(item.GetProperty("executable").GetString() == name && item.GetProperty("sha256").GetString() == actual.Sha256 &&
                item.GetProperty("bytes").GetInt64() == actual.Bytes, "Runtime component integrity failed: " + name);
        }
        var resident = root.GetProperty("resident");
        Require(resident.GetProperty("protocol").GetInt32() == PersistentPlainLyricsRunner.ProtocolVersion && resident.GetProperty("profile").GetString() == PersistentPlainLyricsRunner.ResidentProfileId,
            "Runtime does not contain the production resident profile.");
        Require(resident.GetProperty("sourceSha256").GetString() == scope.GetProperty("runtime").GetProperty("resident").GetProperty("sourceSha256").GetString(),
            "Resident worker manifest differs from the captured native production source.");
        foreach (var (key, name) in new[] { ("cpu", "plain-lyrics-worker.exe"), ("avx2", "plain-lyrics-worker-avx2.exe"), ("vulkan", "plain-lyrics-worker-vulkan.exe") })
        {
            var item = resident.GetProperty(key);
            var actual = runtime.Single(x => x.Path == name);
            Require(item.GetProperty("executable").GetString() == name && item.GetProperty("sha256").GetString() == actual.Sha256 &&
                item.GetProperty("bytes").GetInt64() == actual.Bytes, "Resident runtime component integrity failed: " + name);
        }
        var completionName = config.RuntimeVariant == "avx2" ? "plain-lyrics-worker-avx2.exe" : "plain-lyrics-worker.exe";
        Require(Path.GetFullPath(config.ExecutablePath) == Path.Combine(Path.GetFullPath(config.RuntimeDirectory), completionName) &&
            Identity(config.ExecutablePath) == new FileIdentity(config.ExecutableSha256, config.ExecutableBytes), "Selected executable identity differs.");
        return new(fingerprint, model, runtime);
    }

    private static InputReadLocks LockInputs(CaptureConfiguration config, string configPath, JsonElement scope)
    {
        var paths = new List<string> { configPath, config.ScopePath, config.SourcePath, config.ModelPath };
        paths.AddRange(Directory.EnumerateFiles(config.RuntimeDirectory));
        foreach (var file in scope.GetProperty("sources").GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing source path.");
            paths.Add(Path.Combine(config.RepositoryRoot, relative));
            paths.Add(Path.Combine(config.OutputDirectory, "source-snapshot", relative));
        }
        return new(paths);
    }

    internal static string[] NativeArguments()
    {
        var args = PersistentPlainLyricsRunner.BuildArguments(Path.GetFullPath("model.gguf"), gpu: false).ToArray();
        var model = Array.IndexOf(args, "--model");
        Require(model >= 0, "Production resident argument builder is missing its model parameter.");
        args[model + 1] = "$MODEL";
        return args;
    }
    private static LyricsQuery Query(string phase) => new("production-evidence", "DropSpace original fixture", "source48", TimeSpan.FromSeconds(192), "plain-hy-evidence-" + phase);
    internal static bool SameResult(LyricsTranslationResult a, LyricsTranslationResult b) => a.Outcome == b.Outcome && JsonSerializer.Serialize(a.Document, Json) == JsonSerializer.Serialize(b.Document, Json);
    internal static string UtcNow() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    internal static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string HashFile(string path)
    {
        Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Reparse files are not permitted: " + path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static FileIdentity Identity(string path) => new(HashFile(path), new FileInfo(path).Length);
    internal static void WriteNew(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, value, Json);
        stream.Flush(flushToDisk: true);
    }
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

internal sealed record GpuDefaultProbe(int SchemaVersion, string Kind, bool GpuEnabled, bool Complete,
    string? ActualBackend, bool UsedCpuFallback, string DeviceVendor, string TargetLanguage, int FixtureLineId,
    string SourceText, string Prompt, string? Output, string? Outcome, double ElapsedMilliseconds,
    bool CleanupConfirmed, NativeObservation? ObservedProcess, string RuntimeManifestSha256, CapturedModel Model,
    string FixtureSha256, string SourceFingerprintSha256, string ResidentSourceSha256, string? Error);
internal sealed record CapturedModel(string Id, string Sha256, long Bytes);
internal sealed record ExecutionLimits(int WholeSongSeconds, int PerLineSeconds, int MemoryMiB, int MaximumPromptBytes, int MaximumOutputBytes);
internal sealed record FileIdentity(string Sha256, long Bytes);
internal sealed record RuntimeIdentity(string Path, string Sha256, long Bytes);
internal sealed record SourceIdentity(string Path, string Sha256);
internal sealed record InputSnapshot(string SourceFingerprintSha256, FileIdentity Model, IReadOnlyList<RuntimeIdentity> Runtime);
internal sealed record ProgressObservation(string RequestIdentity, long CacheGeneration, int LineId, int CompletedLineCount,
    int TotalLineCount, bool IsEphemeral, bool IsCurrent, double ElapsedMilliseconds);
internal sealed record SongCheck(string TargetLanguage, string? Outcome, bool Complete, int Calls, double ElapsedMilliseconds, string? Error);
internal sealed record CacheCheck(string TargetLanguage, int AdditionalInferenceCalls, bool MatchesCold, string Outcome, double ElapsedMilliseconds);
internal sealed record NativeObservation(int Id, string StartedAt, string Executable, string ObservedAt);
internal sealed record CancellationCheck(bool CancellationObserved, bool CleanupConfirmed, NativeObservation? ObservedProcess,
    string CancellationRequestedAt, bool CaughtCancellation, bool DrainCompleted, bool ObservedProcessStillAlive,
    bool PromptsRemoved, double ElapsedMilliseconds, string? Error);
internal sealed record CaptureConfiguration(int SchemaVersion, string ModelId, string ModelSha256, long ModelBytes,
    string ExecutableSha256, long ExecutableBytes, string RuntimeManifestSha256, string RuntimeVariant,
    string PromptProfile, string OutputSchema, string PromptVersion, string BackendId, string AcceptanceVersion,
    string SamplerIdentity, ExecutionLimits ExecutionLimits, string FixtureSha256, string SourceFingerprintSha256,
    bool LoadOnly, string CaptureMethod, string[] NativeArguments, string RepositoryRoot, string RuntimeDirectory,
    string ModelPath, string ExecutablePath, string OutputDirectory, string SourcePath, string ScopePath, string LogicalEvidenceRoot,
    bool GpuEnabled, string[] SamplerArguments);

internal sealed record CallRecord(int CallIndex, string Phase, string TargetLanguage, int LineId, string SourceText,
    string Prompt, string? Output, string Status, string StartedAt, double ElapsedMilliseconds, string? Error, NativeObservation? NativeProcess);

/// <summary>Only observes the actual production IPlainLyricsRunner boundary. Does not build or launch a native process.</summary>
internal sealed class ObservedRunner(IPlainLyricsRunner inner, string outputDirectory) : IPlainLyricsRunner
{
    private string _phase = "";
    private string _target = "";
    private LyricsDocument _source = LyricsDocument.Empty;
    private int _nextLine;
    internal int TotalCalls { get; private set; }
    internal List<CallRecord> Calls { get; } = [];
    internal void Begin(string phase, string target, LyricsDocument source)
    {
        _phase = phase; _target = target; _source = source; _nextLine = 0; Calls.Clear();
    }
    public async Task<string> RunPlainAsync(string executablePath, string modelPath, string prompt, string stagingDirectory,
        CancellationToken cancellationToken, string verifiedModelSha256)
    {
        Program.Require(_phase != "cache", "Production cache replay requested new inference; capture refuses to rerun generation.");
        var id = _nextLine++;
        Program.Require(id < _source.Lines.Count && prompt == PlainHyLyricsProtocol.BuildPrompt(_source.Lines[id].Text, _target),
            "Observed production prompt does not match the deterministic source line/target mapping.");
        var index = ++TotalCalls;
        var started = Program.UtcNow();
        var watch = Stopwatch.StartNew();
        string? output = null;
        string? failure = null;
        NativeObservation? nativeProcess = null;
        var status = "returned";
        try
        {
            output = await inner.RunPlainAsync(executablePath, modelPath, prompt, stagingDirectory, cancellationToken, verifiedModelSha256).ConfigureAwait(false);
            if (_phase == "cold") nativeProcess = Program.ObserveProcesses(executablePath).SingleOrDefault();
            return output;
        }
        catch (OperationCanceledException error) { status = "cancelled"; failure = error.ToString(); throw; }
        catch (Exception error) { status = "failed"; failure = error.ToString(); throw; }
        finally
        {
            watch.Stop();
            var record = new CallRecord(index, _phase, _target, id, _source.Lines[id].Text, prompt, output, status, started, watch.Elapsed.TotalMilliseconds, failure, nativeProcess);
            Calls.Add(record);
            // One create-only record per actual invocation preserves partial/failing runs immediately.
            Program.WriteNew(Path.Combine(outputDirectory, $"call-{index:D3}.json"), record);
        }
    }
    public Task DrainCleanupAsync(CancellationToken token) => inner.DrainCleanupAsync(token);
    public void Dispose() => inner.Dispose();
}

/// <summary>Windows read-sharing permits native reads while denying replacement or writes for the full capture.</summary>
internal sealed class InputReadLocks : IDisposable
{
    private readonly List<FileStream> _streams = [];
    internal InputReadLocks(IEnumerable<string> paths)
    {
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                _streams.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var stream in _streams) stream.Dispose();
        _streams.Clear();
    }
}
