using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

if (!OperatingSystem.IsWindows() || args.Length != 2) throw new InvalidOperationException("Explicit cloud Windows diagnostic inputs/output required.");
var inputs = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(12));
var options = new JsonSerializerOptions { WriteIndented = true };
void Save(string name, object value) => File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(value, options));
using var models = new AiModelPackageService(Path.Combine(output, "models"));
var model = AiLyricsModelCatalog.ExperimentalPlain;
var modelPath = await models.DownloadAsync(model.Id, true, null, budget.Token);
using var modelLease = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
var runtime = new AiLyricsRuntimePackage(Assembly.GetExecutingAssembly(), Path.Combine(output, "runtime-cache"));
var executable = await runtime.EnsureResidentWorkerAsync(false, budget.Token);
using var executableLease = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
var workerSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(executableLease, budget.Token));
Save("identity", new {
    diagnosticOnly = true, productionBudgetChanged = false, diagnosticRequestSeconds = 10,
    observedAtUtc = DateTimeOffset.UtcNow, model.Id, model.Sha256, model.Bytes,
    originalProducerRun = 37235696457L, originalProducerHead = "e81e0d55f248afbc865cf17b3f93041a4e7d12a4",
    originalRuntimeArtifact = 11316610580L, originalArchiveSha256 = "344c7aa9016e2f317b6fb157d8bca2e6103c1b1be1ce88fb450816f46589733b",
    originalManifestSha256 = runtime.GetManifestCacheIdentity(), workerSha,
    diagnosticHead = Environment.GetEnvironmentVariable("GITHUB_SHA"), diagnosticRun = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
    phaseTimingObservable = false,
    phaseTimingReason = "Exact original helper emits only ready and a final response; llama/common logs muted, no phase counters exposed. No prefill/decode split can be inferred.",
});
var failed = false;
foreach (var name in new[] { "cross-script", "same-title-version", "uncertain-abstention" })
{
    using var original = JsonDocument.Parse(File.ReadAllText(Path.Combine(inputs, "evidence/selection-probe", name + ".json")));
    var source = original.RootElement;
    var query = source.GetProperty("Query").Deserialize<LyricsQuery>()!;
    var candidates = source.GetProperty("candidates").EnumerateArray().Select(c => LyricsCandidateRules.Describe(
        c.GetProperty("Id").GetString()!, new LyricsDocument([], LyricsProviderKind.NetEase, c.GetProperty("Match").Deserialize<LyricsMatchInfo>()), "zh")).ToArray();
    var snapshot = new LyricsCandidateSnapshot(candidates, Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency);
    if (!LyricsCandidateSelectionProtocol.TryBuild(query, snapshot, "zh", out var prompt)) throw new InvalidDataException("Original fixture prompt cannot be rebuilt.");
    var promptSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
    if (promptSha != source.GetProperty("promptSha256").GetString()) throw new InvalidDataException("Diagnostic prompt differs from original 500ms experiment.");
    LocalInferenceProcess? child = null;
    Task<string>? errors = null;
    string? rawFrame = null, error = null, stderr = null;
    JsonElement? ready = null;
    double? requestMilliseconds = null, preparationMilliseconds = null;
    int? exitCode = null;
    bool cleanupCompleted = false, exitedBeforeCleanup = false;
    try
    {
        CpuInferenceMemoryPolicy.EnsureAvailable(WindowsInferenceProcess.MaximumMemoryBytes, CpuInferenceMemoryPolicy.ReadWindowsSnapshot);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in PersistentPlainLyricsRunner.BuildArguments(modelPath, false, model.Sha256)) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(key => new[] { "LLAMA_", "GGML_", "VK_", "VULKAN_", "CUDA_", "HIP_" }.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray()) start.Environment.Remove(key);
        start.Environment["OMP_NUM_THREADS"] = "4"; start.Environment["OMP_THREAD_LIMIT"] = "4";
        var preparation = Stopwatch.StartNew();
        child = LocalInferenceProcess.Start(start, WindowsInferenceProcess.MaximumMemoryBytes, retainStandardInput: true);
        errors = ReadErrorsAsync(child.StandardError);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        using var startupKill = startup.Token.Register(() => _ = child.TerminateAndWaitForExitAsync());
        using var frame = JsonDocument.Parse(await ReadFrameAsync(child.StandardOutput, startup.Token));
        ready = frame.RootElement.Clone();
        if (!frame.RootElement.GetProperty("ready").GetBoolean() || frame.RootElement.GetProperty("protocol").GetInt32() != 1 ||
            frame.RootElement.GetProperty("selectionProtocol").GetInt32() != 2 || frame.RootElement.GetProperty("backend").GetString() != "cpu") throw new InvalidDataException("Original helper selection handshake invalid.");
        preparationMilliseconds = preparation.Elapsed.TotalMilliseconds;
        var id = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new { protocol = 2, id, prompt }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        requestBudget.CancelAfter(TimeSpan.FromSeconds(10));
        using var requestKill = requestBudget.Token.Register(() => _ = child.TerminateAndWaitForExitAsync());
        var timer = Stopwatch.StartNew();
        try
        {
            await child.StandardInput!.WriteLineAsync(request.AsMemory(), requestBudget.Token);
            await child.StandardInput.FlushAsync(requestBudget.Token);
            rawFrame = await ReadFrameAsync(child.StandardOutput, requestBudget.Token);
            using var response = JsonDocument.Parse(rawFrame);
            if (response.RootElement.GetProperty("protocol").GetInt32() != 2 || response.RootElement.GetProperty("id").GetString() != id) throw new InvalidDataException("Mismatched original helper response.");
        }
        finally { requestMilliseconds = timer.Elapsed.TotalMilliseconds; }
    }
    catch (Exception exception) when (exception is not OutOfMemoryException) { error = exception.ToString(); failed = true; }
    finally
    {
        if (child is not null)
        {
            var childId = child.Process.Id;
            exitedBeforeCleanup = child.Process.HasExited;
            await child.TerminateAndWaitForExitAsync();
            exitCode = child.Process.ExitCode;
            var cleanup = child.CompleteAsync(errors is null ? [] : [errors]);
            await LocalInferenceProcess.WaitForCleanupAsync(cleanup, childId);
            stderr = errors is null ? null : await errors;
            cleanupCompleted = true;
        }
    }
    string? raw = null, actualId = null;
    bool? nativeComplete = null;
    bool validAnswer = false;
    if (rawFrame is not null)
    {
        using var response = JsonDocument.Parse(rawFrame);
        nativeComplete = response.RootElement.GetProperty("complete").GetBoolean();
        raw = response.RootElement.GetProperty("text").GetString();
        raw = LlamaCompletionRunner.RemoveRuntimeTerminator(raw ?? "");
        validAnswer = LyricsCandidateSelectionProtocol.TryParse(raw, candidates, out actualId);
    }
    var expected = source.GetProperty("expectedId").ValueKind == JsonValueKind.Null ? null : source.GetProperty("expectedId").GetString();
    Save(name, new {
        original500msEvidence = source.Clone(), prompt, promptSha, ready, preparationMilliseconds, requestMilliseconds,
        rawFrame, raw, nativeComplete, validAnswer, expectedId = expected, actualId,
        rawMatchesAnnotation = validAnswer && expected == actualId, productionBudgetWouldBeMet = nativeComplete == true && requestMilliseconds <= 500,
        diagnosticOnly = true, error, exitCode, exitedBeforeCleanup, stderr, cleanupCompleted,
        phaseTimingObservable = false, prefillMilliseconds = (double?)null, decodeMilliseconds = (double?)null,
    });
}
Save("summary", new { diagnosticOnly = true, executionCompleted = true, hadExecutionFailure = failed, semanticApproved = false, original500msFailuresRetained = true });
return failed ? 2 : 0;

static async Task<string> ReadFrameAsync(StreamReader reader, CancellationToken token)
{
    var text = new StringBuilder(); var character = new char[1];
    while (await reader.ReadAsync(character.AsMemory(), token) == 1)
    { if (character[0] == '\n') return text.ToString(); if (text.Length >= 4096) throw new InvalidDataException("Diagnostic frame exceeds original protocol bound."); text.Append(character[0]); }
    throw new IOException("Original helper exited before response frame.");
}
static async Task<string> ReadErrorsAsync(StreamReader reader)
{
    var text = new StringBuilder(); var buffer = new char[1024]; int count;
    while ((count = await reader.ReadAsync(buffer)) > 0) if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
    return text.ToString();
}
