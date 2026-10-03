using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DropSpace.Infrastructure.Lyrics;

if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
    throw new PlatformNotSupportedException("This harness requires native Windows x64.");
if (args.Length != 2) throw new ArgumentException("Pass runtime output and QA evidence directories.");
var runtime = Path.GetFullPath(args[0]);
var evidence = Path.GetFullPath(args[1]);
var executable = Path.Combine(runtime, "engine", "dropspace-ct2-helper.exe");
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var results = new List<object>();
var failed = false;
await Run("empty-eof", [], "invalid bounded request");
await Run("invalid-schema", Encoding.UTF8.GetBytes("{}\n"), "invalid request schema");
await Run("invalid-utf8", [0xff, 0x0a], "invalid JSON");
await Run("crlf", Encoding.UTF8.GetBytes("{}\r\n"), "invalid bounded request");
await Run("multiple-frames", Encoding.UTF8.GetBytes("{}\n{}\n"), "multiple requests are not supported");
var unicodeRequest = new
{
    version = 1, source = "ja", target = "en", modelDirectory = evidence,
    sourceTokenizer = Path.Combine(evidence, "不存在.model"), targetTokenizer = Path.Combine(evidence, "missing.model"),
    targetPrefix = (string?)null, tokenizerProtocol = "ArgosSentencePiece", decoderProtocol = "Argos",
    lines = new[] { new { id = 7, text = "世界 こんにちは 안녕 🎵" } }
};
await Run("non-ascii-frame", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(unicodeRequest, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n"), "package path unavailable");
await Run("frozen-native-imports-missing-model", await File.ReadAllBytesAsync(Path.Combine(evidence, "synthetic-request.jsonl")), "CT2 helper failed", requireNative: true);
await Run("blocked-read-timeout", [], null, stopAfter: TimeSpan.FromSeconds(1));
await Run("blocked-read-cancellation", [], null, stopAfter: TimeSpan.FromMilliseconds(750), cancellationProbe: true);
await File.WriteAllTextAsync(Path.Combine(evidence, "native-smoke.json"), JsonSerializer.Serialize(new
{
    status = failed ? "FAILED" : "PASSED", productionReady = false, modelQuality = "NOT TESTED",
    scope = "Actual frozen helper under unchanged production WindowsInferenceProcess; one-process Job and CREATE_NO_WINDOW. Failure/framing/import observation and blocked-read cleanup only.",
    results
}, json));
Environment.ExitCode = failed ? 2 : 0;

async Task Run(string name, byte[] input, string? expectedDiagnostic, bool requireNative = false, TimeSpan? stopAfter = null, bool cancellationProbe = false)
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("--stdio-once");
    foreach (var key in start.Environment.Keys.Where(k => new[] { "PYTHON", "CT2_", "OMP_", "_PYI", "LD_", "DYLD_", "MKL_", "OPENBLAS_" }.Any(prefix => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
        start.Environment.Remove(key);
    start.Environment["OMP_NUM_THREADS"] = "4";
    start.Environment["OMP_THREAD_LIMIT"] = "4";
    start.Environment["MKL_NUM_THREADS"] = "4";
    start.Environment["OPENBLAS_NUM_THREADS"] = "4";
    start.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
    var watch = Stopwatch.StartNew();
    var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    bool windowSeen = false, cleanupConfirmed = false, stopped = false;
    int? exit = null, processId = null;
    string? error = null;
    byte[] output = [], stderr = [];
    LocalInferenceProcess? owner = null;
    Task<byte[]>? outputTask = null, errorTask = null;
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var callerCancellation = new CancellationTokenSource();
    if (cancellationProbe) callerCancellation.CancelAfter(stopAfter!.Value);
    try
    {
        owner = WindowsInferenceProcess.Start(start, retainStandardInput: true);
        var process = owner.Process;
        processId = process.Id;
        outputTask = Capture(owner.StandardOutput.BaseStream, 262144, deadline);
        errorTask = Capture(owner.StandardError.BaseStream, 1048576, deadline);
        if (stopAfter is null)
        {
            // Send bytes exactly, including malformed UTF-8. No text-writer newline conversion.
            await owner.StandardInput!.BaseStream.WriteAsync(input, deadline.Token);
            await owner.StandardInput.BaseStream.FlushAsync(deadline.Token);
            owner.StandardInput.Close();
        }
        while (!process.HasExited)
        {
            deadline.Token.ThrowIfCancellationRequested();
            process.Refresh();
            windowSeen |= process.MainWindowHandle != IntPtr.Zero;
            try
            {
                foreach (ProcessModule module in process.Modules)
                    modules.Add(module.FileName);
            }
            catch (Exception ex) when ((ex is InvalidOperationException or System.ComponentModel.Win32Exception) && process.HasExited) { }
            if (stopAfter is not null && (cancellationProbe ? callerCancellation.IsCancellationRequested : watch.Elapsed >= stopAfter))
            {
                stopped = true;
                await owner.TerminateAndWaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                break;
            }
            await Task.Delay(2, deadline.Token);
        }
        await process.WaitForExitAsync(deadline.Token);
        exit = process.ExitCode;
        output = await outputTask;
        stderr = await errorTask;
    }
    catch (Exception ex) { error = ex.ToString(); }
    finally
    {
        if (owner is not null)
        {
            try
            {
                await LocalInferenceProcess.WaitForCleanupAsync(owner.CompleteAsync((Task?)outputTask ?? Task.CompletedTask, (Task?)errorTask ?? Task.CompletedTask), processId!.Value);
                cleanupConfirmed = true;
            }
            catch (Exception ex) { error = (error ?? "") + "\nCleanup: " + ex; }
        }
    }
    var diagnostic = Encoding.UTF8.GetString(stderr).Trim();
    var filenames = modules.Select(Path.GetFileName).ToArray();
    var nativeObserved = filenames.Contains("ctranslate2.dll", StringComparer.OrdinalIgnoreCase)
        && filenames.Contains("_ext.cp312-win_amd64.pyd", StringComparer.OrdinalIgnoreCase)
        && filenames.Contains("_sentencepiece.cp312-win_amd64.pyd", StringComparer.OrdinalIgnoreCase);
    bool passed = error is null && cleanupConfirmed && !windowSeen && output.Length == 0
        && (stopAfter is null ? exit == 2 && diagnostic == expectedDiagnostic : stopped)
        && (!requireNative || nativeObserved);
    failed |= !passed;
    await File.WriteAllBytesAsync(Path.Combine(evidence, name + ".stdout.bin"), output);
    await File.WriteAllBytesAsync(Path.Combine(evidence, name + ".stderr.bin"), stderr);
    results.Add(new { name, passed, exit, processId, seconds = watch.Elapsed.TotalSeconds, cleanupConfirmed,
        stopped, callerCancellationRequested = callerCancellation.IsCancellationRequested, windowSeen, nativeObserved, modules = modules.Order().ToArray(), error,
        note = requireNative ? "Native modules observed in frozen process; missing model rejection expected. No translation or tokenizer parity claim." : null });
    // Preserve preceding evidence even if a later probe cannot start.
    await File.WriteAllTextAsync(Path.Combine(evidence, "native-smoke-progress.json"), JsonSerializer.Serialize(results, json));
    Console.WriteLine($"{name}: {(passed ? "PASS" : "FAIL")}");
}

static async Task<byte[]> Capture(Stream stream, int limit, CancellationTokenSource stop)
{
    using var captured = new MemoryStream();
    var buffer = new byte[8192];
    while (true)
    {
        var count = await stream.ReadAsync(buffer);
        if (count == 0) break;
        if (captured.Length + count > limit)
        {
            stop.Cancel();
            throw new InvalidDataException("Native stream budget exceeded.");
        }
        captured.Write(buffer, 0, count);
    }
    return captured.ToArray();
}
