using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
public sealed class PersistentPlainLyricsRunner : IPlainLyricsRunner
{
    public const string ResidentProfileId = "hy-q8-plain-resident-v1";
    public const int ProtocolVersion = 1;
    private readonly Func<bool, CancellationToken, Task<string>> _resolve;
    private readonly AiLyricsRuntimeOptions _options;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _idleTimeout;
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
        AiLyricsRuntimeOptions options, TimeSpan idleTimeout)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (idleTimeout <= TimeSpan.Zero || idleTimeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _idleTimeout = idleTimeout;
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
        await _operation.WaitAsync(deadline.Token).ConfigureAwait(false);
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
                        _ = session.Child.TerminateAndWaitForExitAsync();
                        _ = DrainCanceledSessionAsync(session);
                    });
                    deadline.Token.ThrowIfCancellationRequested();
                    var id = Guid.NewGuid().ToString("N");
                    using var stop = deadline.Token.Register(() => session.Child.TerminateAndWaitForExitAsync());
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

    private async Task StartSessionAsync(string modelPath, AiLyricsModelDescriptor model, bool gpu, CancellationToken token)
    {
        await _cleanup.WaitAsync(token).ConfigureAwait(false);
        var executable = await _resolve(gpu, token).ConfigureAwait(false);
        await LocalInferenceProcess.InferenceGate.WaitAsync(token).ConfigureAwait(false);
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
            var child = LocalInferenceProcess.Start(start, memoryBudget, retainStandardInput: true);
            _session = new Session(child, Path.GetFullPath(modelPath), model.Sha256, memoryBudget, gpu);
            gateTransferred = true;
            using var stop = token.Register(() => child.TerminateAndWaitForExitAsync());
            using var ready = await ReadFrameAsync(child.StandardOutput, token).ConfigureAwait(false);
            if (ready.RootElement.GetProperty("protocol").GetInt32() != 1 || !ready.RootElement.GetProperty("ready").GetBoolean() ||
                ready.RootElement.GetProperty("backend").GetString() != (gpu ? "vulkan" : "cpu"))
                throw new InvalidDataException("Unexpected resident runtime handshake.");
            if (model == AiLyricsModelCatalog.ExperimentalLargePlain &&
                (!ready.RootElement.TryGetProperty("modelProfile", out var profile) || profile.GetString() != "hy-mt2-7b-q8"))
                throw new InvalidDataException("The resident worker did not confirm the selected 7B resource profile.");
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
        internal Task Errors { get; }
        internal Task Memory { get; }
        private static async Task DrainErrorsAsync(StreamReader reader)
        {
            var buffer = new char[2048];
            while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { }
        }
        private static async Task MonitorAsync(LocalInferenceProcess child, long memoryBudget)
        {
            while (!child.Process.HasExited)
            {
                child.Process.Refresh();
                if (child.Process.WorkingSet64 > memoryBudget)
                { await child.TerminateAndWaitForExitAsync().ConfigureAwait(false); return; }
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }
}
