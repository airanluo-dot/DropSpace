using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class AiLyricsRuntimeOptions
{
    private volatile bool _gpuEnabled = true;
    /// <summary>GPU preference defaults on; AI itself remains opt-in. Cancel and drain before changing.</summary>
    public bool GpuEnabled { get => _gpuEnabled; set => _gpuEnabled = value; }
}

/// <summary>One bounded private native worker retaining model weights between lyric requests.
/// No listening port or user-visible service. Cancellation/maintenance drains owned OS exit.</summary>
public sealed class PersistentPlainLyricsRunner : IPlainLyricsRunner, ILyricsSelectionRuntime
{
    public const string ResidentProfileId = "hy-q8-plain-resident-v1";
    public const int ProtocolVersion = 1;
    public const int SelectionProtocolVersion = 2;
    private readonly Func<bool, CancellationToken, Task<string>> _resolve;
    private readonly AiLyricsRuntimeOptions _options;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private int _translationWaiters;
    private CancellationTokenSource? _preparation;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _idleTimeout;
    private readonly Func<CpuMemorySnapshot?> _readMemorySnapshot;
    private Session? _session;
    private Task _cleanup = Task.CompletedTask;
    private long _generation;
    private bool _gpuFailed;
    private bool _lastGpuSetting;
    private string? _lastModelSha256;
    private volatile bool _disposed;
    private string? _lastExecutionBackend;
    private volatile bool _lastExecutionUsedCpuFallback;
    /// <summary>Observed from a completed, protocol-validated response, not a requested preference.</summary>
    public string? LastExecutionBackend => Volatile.Read(ref _lastExecutionBackend);
    public bool LastExecutionUsedCpuFallback => _lastExecutionUsedCpuFallback;

    public PersistentPlainLyricsRunner(AiLyricsRuntimePackage runtime, AiLyricsRuntimeOptions options)
        : this((gpu, token) => runtime.EnsureResidentWorkerAsync(gpu, token), options, TimeSpan.FromSeconds(60)) { }

    internal PersistentPlainLyricsRunner(Func<bool, CancellationToken, Task<string>> resolve,
        AiLyricsRuntimeOptions options, TimeSpan idleTimeout, Func<CpuMemorySnapshot?>? readMemorySnapshot = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (idleTimeout <= TimeSpan.Zero || idleTimeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _idleTimeout = idleTimeout;
        _readMemorySnapshot = readMemorySnapshot ?? CpuInferenceMemoryPolicy.ReadWindowsSnapshot;
    }

    public static IReadOnlyList<string> BuildArguments(string modelPath, bool gpu) =>
        BuildArguments(modelPath, gpu, AiLyricsModelCatalog.ExperimentalPlain.Sha256);

    public static IReadOnlyList<string> BuildArguments(string modelPath, bool gpu, string verifiedModelSha256)
    {
        var model = AiLyricsModelCatalog.FindSelectableByHash(verifiedModelSha256) ??
            throw new InvalidDataException("The resident plaintext runtime requires a pinned model.");
        var arguments = new List<string> { "--model", Path.GetFullPath(modelPath), "--mode", gpu ? "vulkan" : "cpu" };
        if (model == AiLyricsModelCatalog.ExperimentalLargePlain)
            arguments.AddRange(["--model-profile", "hy-mt2-7b-q8"]);
        return arguments.AsReadOnly();
    }

    public async Task<string> RunPlainAsync(string executablePath, string modelPath, string prompt, string stagingDirectory,
        CancellationToken cancellationToken, string verifiedModelSha256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(prompt);
        var model = AiLyricsModelCatalog.FindSelectableByHash(verifiedModelSha256) ??
            throw new InvalidDataException("The resident plaintext runtime requires a verified Hy Q8 model.");
        if (Encoding.UTF8.GetByteCount(prompt) is 0 or > PlainHyLyricsProtocol.MaximumPromptBytes)
            throw new InvalidDataException("Prompt exceeds the resident runtime budget.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(70));
        Interlocked.Increment(ref _translationWaiters);
        try { _ = Volatile.Read(ref _preparation)?.CancelAsync(); }
        catch (ObjectDisposedException) { }
        try { await _operation.WaitAsync(deadline.Token).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _translationWaiters); }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            Volatile.Write(ref _lastExecutionBackend, null);
            _lastExecutionUsedCpuFallback = false;
            var gpu = _options.GpuEnabled;
            if (gpu != _lastGpuSetting || model.Sha256 != _lastModelSha256)
            {
                await StopSessionAsync().ConfigureAwait(false);
                _gpuFailed = false;
                _lastGpuSetting = gpu;
                _lastModelSha256 = model.Sha256;
            }
            gpu &= !_gpuFailed;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (_session is not null && (_session.Gpu != gpu || _session.Model != Path.GetFullPath(modelPath) ||
                        _session.ModelSha256 != model.Sha256 || _session.Child.Process.HasExited))
                        await StopSessionAsync().ConfigureAwait(false);
                    if (_session is null) await StartSessionAsync(modelPath, model, gpu, deadline.Token).ConfigureAwait(false);
                    var session = _session!;
                    session.Cancellation.Dispose();
                    // Retain the caller's song token through the resident idle period. A late
                    // cancellation targets only this owned session, never a replacement worker.
                    session.Cancellation = cancellationToken.Register(() =>
                    {
                        Interlocked.Exchange(ref session.StopRequested, 1);
                        _ = session.Child.TerminateAndWaitForExitAsync();
                        _ = DrainCanceledSessionAsync(session);
                    });
                    deadline.Token.ThrowIfCancellationRequested();
                    var id = Guid.NewGuid().ToString("N");
                    using var stop = deadline.Token.Register(() =>
                    { Interlocked.Exchange(ref session.StopRequested, 1); _ = session.Child.TerminateAndWaitForExitAsync(); });
                    var request = JsonSerializer.Serialize(new { protocol = 1, id, prompt });
                    await session.Child.StandardInput!.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
                    await session.Child.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                    using var response = await ReadFrameAsync(session.Child.StandardOutput, deadline.Token).ConfigureAwait(false);
                    var root = response.RootElement;
                    if (root.GetProperty("protocol").GetInt32() != 1 || root.GetProperty("id").GetString() != id ||
                        !root.GetProperty("complete").GetBoolean()) throw new InvalidDataException("Invalid or incomplete resident inference response.");
                    var output = root.GetProperty("text").GetString() ?? throw new InvalidDataException("Missing resident inference text.");
                    if (Encoding.UTF8.GetByteCount(output) > PlainHyLyricsProtocol.MaximumOutputBytes)
                        throw new InvalidDataException("Resident inference output exceeds budget.");
                    deadline.Token.ThrowIfCancellationRequested();
                    Volatile.Write(ref _lastExecutionBackend, session.Gpu ? "vulkan" : "cpu");
                    _lastExecutionUsedCpuFallback = _options.GpuEnabled && !session.Gpu;
                    var generation = ++_generation;
                    _ = ReleaseWhenIdleAsync(generation);
                    return LlamaCompletionRunner.RemoveRuntimeTerminator(output);
                }
                catch (Exception error)
                {
                    // A fallback is impossible until exit, reader settlement and Job/handle release
                    // have all been confirmed. Cleanup timeout keeps the global gate closed.
                    await StopSessionAsync().ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!gpu || attempt != 0 || error is InvalidDataException or JsonException or KeyNotFoundException) throw;
                    _gpuFailed = true;
                    gpu = false;
                }
            }
        }
        finally { _operation.Release(); }
    }

    public async Task<bool> PrepareSelectionAsync(string verifiedModelPath, string modelHash, CancellationToken token)
    {
        var model = AiLyricsModelCatalog.FindSelectableByHash(modelHash);
        if (model is null || _disposed || Volatile.Read(ref _translationWaiters) != 0 || !_operation.Wait(0)) return false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        stop.CancelAfter(TimeSpan.FromSeconds(60));
        Volatile.Write(ref _preparation, stop);
        try
        {
            if (_disposed || Volatile.Read(ref _translationWaiters) != 0 || !_cleanup.IsCompletedSuccessfully) return false;
            if (_session is { } existing)
            {
                if (!CanSelect(existing, modelHash) || existing.Model != Path.GetFullPath(verifiedModelPath)) return false;
            }
            else
            {
                _lastGpuSetting = _options.GpuEnabled;
                _lastModelSha256 = modelHash;
                _gpuFailed = false;
                await StartSessionAsync(verifiedModelPath, model, _options.GpuEnabled, stop.Token, nonblocking: true).ConfigureAwait(false);
            }
            stop.Token.ThrowIfCancellationRequested();
            if (_session is not { } ready || !CanSelect(ready, modelHash))
            { await StopSessionAsync().ConfigureAwait(false); return false; }
            _ = ReleaseWhenIdleAsync(++_generation);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { await StopSessionAsync().ConfigureAwait(false); return false; }
        finally { Volatile.Write(ref _preparation, null); _operation.Release(); }
    }

    private bool CanSelect(Session session, string modelHash) => !_disposed && session.SelectionSupported && _cleanup.IsCompletedSuccessfully &&
        Volatile.Read(ref session.StopRequested) == 0 && !session.Child.Process.HasExited &&
        session.ModelSha256 == modelHash && _lastGpuSetting == _options.GpuEnabled && session.Gpu == (_options.GpuEnabled && !_gpuFailed);

    public bool IsSelectionWarm(string modelHash)
    {
        if (_disposed || Volatile.Read(ref _translationWaiters) != 0 || !_operation.Wait(0)) return false;
        try { return _session is { } session && CanSelect(session, modelHash); }
        finally { _operation.Release(); }
    }

    public async Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(prompt) is 0 or > LyricsCandidateSelectionProtocol.MaximumPromptBytes ||
            _disposed || Volatile.Read(ref _translationWaiters) != 0 || !_operation.Wait(0)) return null;
        try
        {
            if (Volatile.Read(ref _translationWaiters) != 0 || _session is not { } session || !CanSelect(session, modelHash)) return null;
            token.ThrowIfCancellationRequested();
            session.Cancellation.Dispose();
            if (!CanSelect(session, modelHash)) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            using var registration = deadline.Token.Register(() =>
            { Interlocked.Exchange(ref session.StopRequested, 1); _ = session.Child.TerminateAndWaitForExitAsync(); });
            try
            {
                // The native helper clears KV memory and resets the sampler per request.
                // This is the independent selector prompt, never translation output.
                var id = Guid.NewGuid().ToString("N");
                var request = JsonSerializer.Serialize(new { protocol = SelectionProtocolVersion, id, prompt },
                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                await session.Child.StandardInput!.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
                await session.Child.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                using var response = await ReadFrameAsync(session.Child.StandardOutput, deadline.Token).ConfigureAwait(false);
                var root = response.RootElement;
                if (root.GetProperty("protocol").GetInt32() != SelectionProtocolVersion || root.GetProperty("id").GetString() != id ||
                    !root.GetProperty("complete").GetBoolean()) throw new InvalidDataException("Invalid selection frame.");
                var output = LlamaCompletionRunner.RemoveRuntimeTerminator(root.GetProperty("text").GetString() ?? string.Empty);
                if (Encoding.UTF8.GetByteCount(output) > LyricsCandidateSelectionProtocol.MaximumOutputBytes)
                    throw new InvalidDataException("Selection output exceeds budget.");
                deadline.Token.ThrowIfCancellationRequested();
                _ = ReleaseWhenIdleAsync(++_generation);
                return output;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { await StopSessionAsync().ConfigureAwait(false); throw; }
        }
        finally { _operation.Release(); }
    }

    private async Task StartSessionAsync(string modelPath, AiLyricsModelDescriptor model, bool gpu, CancellationToken token, bool nonblocking = false)
    {
        await _cleanup.WaitAsync(token).ConfigureAwait(false);
        var executable = await _resolve(gpu, token).ConfigureAwait(false);
        if (nonblocking)
        {
            if (Volatile.Read(ref _translationWaiters) != 0 || !LocalInferenceProcess.InferenceGate.Wait(0))
                throw new IOException("Inference is busy.");
        }
        else await LocalInferenceProcess.InferenceGate.WaitAsync(token).ConfigureAwait(false);
        var gateTransferred = false;
        try
        {
            var start = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in BuildArguments(modelPath, gpu, model.Sha256)) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(key =>
                key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("VULKAN_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CUDA_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("HIP_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["OMP_NUM_THREADS"] = "4";
            start.Environment["OMP_THREAD_LIMIT"] = "4";
            var memoryBudget = LlamaCompletionRunner.MemoryBudgetFor(model.Sha256);
            token.ThrowIfCancellationRequested();
            // The old worker has fully exited and this owner holds the global gate.
            // GPU fallback gets a fresh reading here; an existing resident is reused
            // without pretending that its already allocated weights are still free RAM.
            if (!gpu) CpuInferenceMemoryPolicy.EnsureAvailable(memoryBudget, _readMemorySnapshot);
            token.ThrowIfCancellationRequested();
            var child = LocalInferenceProcess.Start(start, memoryBudget, retainStandardInput: true);
            _session = new Session(child, Path.GetFullPath(modelPath), model.Sha256, memoryBudget, gpu);
            gateTransferred = true;
            var starting = _session;
            using var stop = token.Register(() =>
            { Interlocked.Exchange(ref starting.StopRequested, 1); _ = child.TerminateAndWaitForExitAsync(); });
            using var ready = await ReadFrameAsync(child.StandardOutput, token).ConfigureAwait(false);
            if (ready.RootElement.GetProperty("protocol").GetInt32() != 1 || !ready.RootElement.GetProperty("ready").GetBoolean() ||
                ready.RootElement.GetProperty("backend").GetString() != (gpu ? "vulkan" : "cpu"))
                throw new InvalidDataException("Unexpected resident runtime handshake.");
            if (model == AiLyricsModelCatalog.ExperimentalLargePlain &&
                (!ready.RootElement.TryGetProperty("modelProfile", out var profile) || profile.GetString() != "hy-mt2-7b-q8"))
                throw new InvalidDataException("The resident worker did not confirm the selected 7B resource profile.");
            _session.SelectionSupported = ready.RootElement.TryGetProperty("selectionProtocol", out var selectionProtocol) &&
                selectionProtocol.ValueKind == JsonValueKind.Number && selectionProtocol.TryGetInt32(out var selectorVersion) &&
                selectorVersion == SelectionProtocolVersion;
        }
        finally { if (!gateTransferred) LocalInferenceProcess.InferenceGate.Release(); }
    }

    private async Task StopSessionAsync()
    {
        ++_generation;
        var session = _session;
        if (session is not null)
        {
            _session = null;
            Interlocked.Exchange(ref session.StopRequested, 1);
            session.Cancellation.Dispose();
            var id = session.Child.Process.Id;
            _cleanup = session.Child.CompleteAsync(session.Errors, session.Memory);
            _ = LlamaCompletionRunner.ReleaseGateAfterCleanupAsync(_cleanup, LocalInferenceProcess.InferenceGate);
            await LocalInferenceProcess.WaitForCleanupAsync(_cleanup, id).ConfigureAwait(false);
        }
        else await _cleanup.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    private async Task DrainCanceledSessionAsync(Session expected)
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try { if (ReferenceEquals(_session, expected)) await StopSessionAsync().ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceError("Canceled resident session cleanup remains owned: {0}", error.GetType().Name); }
        finally { _operation.Release(); }
    }

    public async Task DrainCleanupAsync(CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try { await StopSessionAsync().ConfigureAwait(false); }
        finally { _operation.Release(); }
    }

    private async Task ReleaseWhenIdleAsync(long generation)
    {
        try
        {
            await Task.Delay(_idleTimeout, _lifetime.Token).ConfigureAwait(false);
            await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try { if (generation == _generation) await StopSessionAsync().ConfigureAwait(false); }
            finally { _operation.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Trace.TraceError("Resident inference cleanup remains owned: {0}", error.GetType().Name); }
    }

    private static async Task<JsonDocument> ReadFrameAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) == 1)
        {
            if (buffer[0] == '\n') return JsonDocument.Parse(text.ToString());
            if (text.Length >= 131_072) throw new InvalidDataException("Resident runtime frame exceeds budget.");
            text.Append(buffer[0]);
        }
        throw new IOException("Resident inference exited before completing its response.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // This task retains native ownership; callers drain before destructive maintenance.
        _ = DrainDisposedAsync();
    }

    private async Task DrainDisposedAsync()
    {
        try { await DrainCleanupAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceError("Resident inference shutdown remains owned: {0}", error.GetType().Name); }
    }

    private sealed class Session
    {
        internal Session(LocalInferenceProcess child, string model, string modelSha256, long memoryBudget, bool gpu)
        {
            Child = child; Model = model; ModelSha256 = modelSha256; Gpu = gpu;
            Errors = DrainErrorsAsync(child.StandardError);
            Memory = MonitorAsync(child, memoryBudget);
        }
        internal LocalInferenceProcess Child { get; }
        internal string Model { get; }
        internal string ModelSha256 { get; }
        internal bool Gpu { get; }
        internal CancellationTokenRegistration Cancellation { get; set; }
        internal int StopRequested;
        internal bool SelectionSupported;
        internal Task Errors { get; }
        internal Task Memory { get; }
        private static async Task DrainErrorsAsync(StreamReader reader)
        {
            var buffer = new char[2048];
            while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { }
        }
        private async Task MonitorAsync(LocalInferenceProcess child, long memoryBudget)
        {
            while (!child.Process.HasExited)
            {
                child.Process.Refresh();
                if (child.Process.WorkingSet64 > memoryBudget)
                { Interlocked.Exchange(ref StopRequested, 1); await child.TerminateAndWaitForExitAsync().ConfigureAwait(false); return; }
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }
}
