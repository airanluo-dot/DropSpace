using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class AiLyricsRuntimeOptions
{
    private int _gpuEnabled = 1;
    private long _gpuSettingGeneration;
    /// <summary>GPU preference defaults on; AI itself remains opt-in. Cancel and drain before changing.</summary>
    public bool GpuEnabled
    {
        get => Volatile.Read(ref _gpuEnabled) != 0;
        set
        {
            var enabled = value ? 1 : 0;
            if (Interlocked.Exchange(ref _gpuEnabled, enabled) != enabled)
                Interlocked.Increment(ref _gpuSettingGeneration);
        }
    }
    private int _backend;
    public LyricsGpuBackend Backend
    {
        get => (LyricsGpuBackend)Volatile.Read(ref _backend);
        set
        {
            var selected = Enum.IsDefined(value) ? (int)value : 0;
            if (Interlocked.Exchange(ref _backend, selected) != selected) NotifyBackendChanged();
        }
    }
    public void NotifyBackendChanged() => Interlocked.Increment(ref _gpuSettingGeneration);
    // Reapplying the same preference during a role/profile switch retains fallback.
    internal long GpuSettingGeneration => Interlocked.Read(ref _gpuSettingGeneration);
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
    private string _gpuBackend;
    private CudaLyricsRuntimePackage? _automaticCuda;
    private readonly Func<CancellationToken, Task<CudaLyricsComponentLease>>? _openCudaLease;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private int _translationWaiters;
    private CancellationTokenSource? _preparation;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<CpuMemorySnapshot?> _readMemorySnapshot;
    private Session? _session;
    private Task _cleanup = Task.CompletedTask;
    private long _generation;
    // Accessed only under _operation. A bounded cooldown avoids a failed GPU launch per
    // lyric line; changed preferences or an explicit retry clear it immediately.
    private readonly Dictionary<string, long> _gpuFailedModels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan GpuRetryDelay = TimeSpan.FromMinutes(1);
    private bool _lastGpuSetting;
    private long _lastGpuSettingGeneration = -1;
    private volatile bool _disposed;
    private PlainLyricsExecutionStatus? _lastExecution;
    private PlainLyricsCurrentExecution? _currentExecution;
    private PlainLyricsRuntimeFailure? _lastFailure;
    private ILogger<PersistentPlainLyricsRunner> _logger = NullLogger<PersistentPlainLyricsRunner>.Instance;
    public event EventHandler? ExecutionStatusChanged;
    public PlainLyricsCurrentExecution? CurrentExecution
    {
        get
        {
            var current = Volatile.Read(ref _currentExecution);
            if (current?.PreferenceGeneration != _options.GpuSettingGeneration) return null;
            var session = Volatile.Read(ref _session);
            if (current?.Phase == PlainLyricsExecutionPhase.Executing &&
                (session is null || Volatile.Read(ref session.StopRequested) != 0 || ReadObservedExitCode(session) is not null)) return null;
            return current;
        }
    }
    public PlainLyricsRuntimeFailure? LastFailure
    {
        get
        {
            var failure = Volatile.Read(ref _lastFailure);
            return failure?.PreferenceGeneration == _options.GpuSettingGeneration ? failure : null;
        }
    }
    public void InvalidateExecutionStatus()
    {
        Volatile.Write(ref _currentExecution, null);
        Volatile.Write(ref _lastExecution, null);
        Volatile.Write(ref _lastFailure, null);
        ExecutionStatusChanged?.Invoke(this, EventArgs.Empty);
    }
    // Optional diagnostic notification after the actual pipe write/flush. No prompt or lyric data.
    public event Action? SelectionRequestSent;
    internal event Action? TranslationRequestSent;
    internal int? ActiveProcessId => Volatile.Read(ref _session)?.Child.Process.Id;
    internal JsonElement? ActiveDevice => Volatile.Read(ref _session)?.Device;
    /// <summary>Observed from a completed, protocol-validated response, not a requested preference.</summary>
    public PlainLyricsExecutionStatus? LastExecutionStatus
    {
        get
        {
            var observed = Volatile.Read(ref _lastExecution);
            return observed?.PreferenceGeneration == _options.GpuSettingGeneration ? observed : null;
        }
    }
    public string? LastExecutionBackend => LastExecutionStatus?.Backend;
    public bool LastExecutionUsedCpuFallback => LastExecutionStatus?.UsedCpuFallback == true;

    public PersistentPlainLyricsRunner(AiLyricsRuntimePackage runtime, AiLyricsRuntimeOptions options)
        : this((gpu, token) => runtime.EnsureResidentWorkerAsync(gpu, token), options, TimeSpan.FromSeconds(60)) { }

    /// <summary>Opt-in CUDA component; fallback resolves only the separately trusted shipping CPU worker.
    /// Each runner owns its backend/model failure state. Selected automatically by shipping DI on compatible NVIDIA drivers.</summary>
    public PersistentPlainLyricsRunner(AiLyricsRuntimePackage runtime, CudaLyricsRuntimePackage cuda,
        AiLyricsRuntimeOptions options)
        : this((gpu, token) => gpu ? cuda.EnsureWorkerAsync(token) : runtime.EnsureResidentWorkerAsync(false, token),
            options, TimeSpan.FromSeconds(60), gpuBackend: "cuda")
    { _openCudaLease = cuda.OpenWorkerLeaseAsync; }

    internal PersistentPlainLyricsRunner(Func<bool, CancellationToken, Task<string>> resolve,
        AiLyricsRuntimeOptions options, TimeSpan idleTimeout, Func<CpuMemorySnapshot?>? readMemorySnapshot = null,
        string gpuBackend = "vulkan", TimeSpan? requestTimeout = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (gpuBackend is not ("vulkan" or "cuda")) throw new ArgumentOutOfRangeException(nameof(gpuBackend));
        _gpuBackend = gpuBackend;
        if (idleTimeout <= TimeSpan.Zero || idleTimeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _idleTimeout = idleTimeout;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _readMemorySnapshot = readMemorySnapshot ?? CpuInferenceMemoryPolicy.ReadWindowsSnapshot;
    }

    public static PersistentPlainLyricsRunner CreateAutomatic(AiLyricsRuntimePackage runtime,
        CudaLyricsRuntimePackage cuda, AiLyricsRuntimeOptions options,
        ILogger<PersistentPlainLyricsRunner>? logger = null)
    {
        var runner = new PersistentPlainLyricsRunner(runtime, options)
        { _automaticCuda = cuda, _logger = logger ?? NullLogger<PersistentPlainLyricsRunner>.Instance };
        return runner;
    }

    public static IReadOnlyList<string> BuildArguments(string modelPath, bool gpu) =>
        BuildArguments(modelPath, gpu, AiLyricsModelCatalog.ExperimentalPlain.Sha256);

    public static IReadOnlyList<string> BuildArguments(string modelPath, bool gpu, string verifiedModelSha256)
    {
        return BuildArguments(modelPath, gpu, verifiedModelSha256, "vulkan");
    }

    internal static IReadOnlyList<string> BuildArguments(string modelPath, bool gpu, string verifiedModelSha256, string gpuBackend)
    {
        if (gpuBackend is not ("vulkan" or "cuda")) throw new ArgumentOutOfRangeException(nameof(gpuBackend));
        var model = AiLyricsModelCatalog.FindSelectableByHash(verifiedModelSha256) ??
            throw new InvalidDataException("The resident plaintext runtime requires a pinned model.");
        var arguments = new List<string> { "--model", Path.GetFullPath(modelPath), "--mode", gpu ? gpuBackend : "cpu" };
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
        deadline.CancelAfter(_requestTimeout + TimeSpan.FromSeconds(10));
        Interlocked.Increment(ref _translationWaiters);
        try { _ = Volatile.Read(ref _preparation)?.CancelAsync(); }
        catch (ObjectDisposedException) { }
        try { await WaitForGateAsync(_operation, deadline.Token, PlainLyricsMetrics.Stage.OperationQueue).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested &&
            !_lifetime.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The resident translation operation queue exceeded its deadline.", error); }
        finally { Interlocked.Decrement(ref _translationWaiters); }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            deadline.CancelAfter(_requestTimeout);
            if (_lastGpuSettingGeneration != _options.GpuSettingGeneration)
            {
                await StopSessionAsync().ConfigureAwait(false);
                ObserveGpuSetting();
            }
            var gpu = ShouldUseGpu(model.Sha256);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (_session is not null && (_session.Gpu != gpu || _session.Model != Path.GetFullPath(modelPath) ||
                        _session.ModelSha256 != model.Sha256 || _session.Child.Process.HasExited))
                        await StopSessionAsync().ConfigureAwait(false);
                    BeginExecution(model.Sha256, "translation");
                    if (_session is null) await StartSessionAsync(modelPath, model, gpu, deadline.Token).ConfigureAwait(false);
                    else PlainLyricsMetrics.Count(PlainLyricsMetrics.Event.ResidentReuse);
                    var session = _session!;
                    MarkExecuting(session, "translation");
                    session.Cancellation.Dispose();
                    // Retain the caller's song token through the resident idle period. A late
                    // cancellation targets only this owned session, never a replacement worker.
                    session.Cancellation = cancellationToken.Register(() =>
                    {
                        session.MarkCancelled();
                        _ = session.Child.TerminateAndWaitForExitAsync();
                        _ = DrainCanceledSessionAsync(session);
                    });
                    deadline.Token.ThrowIfCancellationRequested();
                    using var call = PlainLyricsMetrics.Measure(PlainLyricsMetrics.Stage.Call, deadline.Token);
                    var id = Guid.NewGuid().ToString("N");
                    using var stop = deadline.Token.Register(() =>
                    { session.MarkCancelled(); _ = session.Child.TerminateAndWaitForExitAsync(); });
                    var request = JsonSerializer.Serialize(new { protocol = 1, id, prompt });
                    await session.Child.StandardInput!.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
                    await session.Child.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                    TranslationRequestSent?.Invoke();
                    using var response = await ReadFrameAsync(session.Child.StandardOutput, deadline.Token).ConfigureAwait(false);
                    var root = response.RootElement;
                    if (root.GetProperty("protocol").GetInt32() != 1 || root.GetProperty("id").GetString() != id ||
                        !root.GetProperty("complete").GetBoolean()) throw new InvalidDataException("Invalid or incomplete resident inference response.");
                    var output = root.GetProperty("text").GetString() ?? throw new InvalidDataException("Missing resident inference text.");
                    if (Encoding.UTF8.GetByteCount(output) > PlainHyLyricsProtocol.MaximumOutputBytes)
                        throw new InvalidDataException("Resident inference output exceeds budget.");
                    output = LlamaCompletionRunner.RemoveRuntimeTerminator(output);
                    if (!PlainHyLyricsProtocol.IsCompleteLine(output))
                        throw new InvalidDataException("Resident inference did not return a complete line.");
                    deadline.Token.ThrowIfCancellationRequested();
                    RecordExecution(session, "translation");
                    if (LastExecutionUsedCpuFallback) PlainLyricsMetrics.Count(PlainLyricsMetrics.Event.FallbackUse);
                    call.Complete();
                    var generation = ++_generation;
                    _ = ReleaseWhenIdleAsync(generation);
                    return output;
                }
                catch (Exception error)
                {
                    // A fallback is impossible until exit, reader settlement and Job/handle release
                    // have all been confirmed. Cleanup timeout keeps the global gate closed.
                    var failedSession = _session;
                    await ObserveNaturalExitAsync(failedSession, error).ConfigureAwait(false);
                    var fallback = !deadline.IsCancellationRequested && gpu && attempt == 0 &&
                        error is not (OutOfMemoryException or InvalidDataException or JsonException or KeyNotFoundException);
                    try { await StopSessionAsync().ConfigureAwait(false); }
                    finally
                    {
                        if (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                            RecordFailure(model.Sha256, gpu, failedSession, error, deadline.IsCancellationRequested,
                                fallback && !deadline.IsCancellationRequested && _cleanup.IsCompletedSuccessfully);
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!fallback) throw;
                    _gpuFailedModels[model.Sha256] = Stopwatch.GetTimestamp();
                    PlainLyricsMetrics.Count(PlainLyricsMetrics.Event.FallbackAttempt);
                    gpu = false;
                }
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested &&
            !_lifetime.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // Caller/song and runtime-lifetime cancellation must still propagate. Only
            // this request's private deadline is a failed line, so the song coordinator
            // can retain already validated progressive output after native cleanup.
            throw new TimeoutException("The resident translation request exceeded its deadline.", error);
        }
        finally { EndExecution(); _operation.Release(); }
    }

    public async Task<bool> PrepareSelectionAsync(string verifiedModelPath, string modelHash, CancellationToken token)
    {
        var model = AiLyricsSelectionModelCatalog.FindSelectableByHash(modelHash);
        if (model is null || _disposed || Volatile.Read(ref _translationWaiters) != 0 || !_operation.Wait(0)) return false;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        stop.CancelAfter(TimeSpan.FromSeconds(60));
        Volatile.Write(ref _preparation, stop);
        try
        {
            if (_disposed || Volatile.Read(ref _translationWaiters) != 0 || !_cleanup.IsCompletedSuccessfully) return false;
            if (_session is { } existing)
            {
                // Replacing the same unsupported profile cannot add selector capability;
                // retain a usable translation resident instead of restarting it each song.
                if (!existing.SelectionSupported && existing.ModelSha256 == modelHash &&
                    existing.Model == Path.GetFullPath(verifiedModelPath)) return false;
                if (!CanSelect(existing, modelHash) || existing.Model != Path.GetFullPath(verifiedModelPath))
                {
                    // Independent roles still share one native owner. A different resident
                    // must confirm exit and release its resources before replacement starts.
                    await StopSessionAsync().ConfigureAwait(false);
                    stop.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _translationWaiters) != 0) return false;
                }
            }
            if (_session is null)
            {
                ObserveGpuSetting();
                var gpu = ShouldUseGpu(model.Sha256);
                try { await StartSessionAsync(verifiedModelPath, model, gpu, stop.Token, nonblocking: true).ConfigureAwait(false); }
                catch (Exception error) when (gpu && !stop.IsCancellationRequested &&
                    error is not (OutOfMemoryException or InvalidDataException or JsonException or KeyNotFoundException))
                {
                    // Preparation may recover from an unavailable GPU in the background.
                    // Exit/cleanup precedes CPU admission; neither attempt queues for a gate.
                    var failedSession = _session;
                    await ObserveNaturalExitAsync(failedSession, error).ConfigureAwait(false);
                    try { await StopSessionAsync().ConfigureAwait(false); }
                    finally { RecordFailure(model.Sha256, true, failedSession, error, false, _cleanup.IsCompletedSuccessfully); }
                    stop.Token.ThrowIfCancellationRequested();
                    _gpuFailedModels[model.Sha256] = Stopwatch.GetTimestamp();
                    await StartSessionAsync(verifiedModelPath, model, false, stop.Token, nonblocking: true).ConfigureAwait(false);
                }
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

    private void ObserveGpuSetting()
    {
        var generation = _options.GpuSettingGeneration;
        if (_lastGpuSettingGeneration == generation) return;
        _gpuFailedModels.Clear();
        _lastGpuSetting = _options.GpuEnabled;
        _lastGpuSettingGeneration = generation;
    }

    private bool ShouldUseGpu(string modelHash) => _options.GpuEnabled &&
        (!_gpuFailedModels.TryGetValue(modelHash, out var failedAt) || Stopwatch.GetElapsedTime(failedAt) >= GpuRetryDelay);

    private bool CanSelect(Session session, string modelHash) => !_disposed && session.SelectionSupported && _cleanup.IsCompletedSuccessfully &&
        Volatile.Read(ref session.StopRequested) == 0 && !session.Child.Process.HasExited &&
        session.ModelSha256 == modelHash && _lastGpuSetting == _options.GpuEnabled &&
        _lastGpuSettingGeneration == _options.GpuSettingGeneration &&
        session.Gpu == ShouldUseGpu(modelHash);

    public bool IsSelectionWarm(string modelHash)
    {
        if (AiLyricsSelectionModelCatalog.FindSelectableByHash(modelHash) is null ||
            _disposed || Volatile.Read(ref _translationWaiters) != 0 || !_operation.Wait(0)) return false;
        try { return _session is { } session && CanSelect(session, modelHash); }
        finally { _operation.Release(); }
    }

    public bool CanPrepareSelection => !_disposed && Volatile.Read(ref _translationWaiters) == 0 &&
        _operation.CurrentCount > 0 && _cleanup.IsCompletedSuccessfully;

    public async Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token)
    {
        if (AiLyricsSelectionModelCatalog.FindSelectableByHash(modelHash) is null ||
            Encoding.UTF8.GetByteCount(prompt) is 0 or > LyricsCandidateSelectionProtocol.MaximumPromptBytes ||
            _disposed || Volatile.Read(ref _translationWaiters) != 0 || !_operation.Wait(0)) return null;
        try
        {
            if (Volatile.Read(ref _translationWaiters) != 0 || _session is not { } session || !CanSelect(session, modelHash)) return null;
            token.ThrowIfCancellationRequested();
            session.Cancellation.Dispose();
            if (!CanSelect(session, modelHash)) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            // The host links its remaining snapshot deadline. Keep the same absolute
            // ceiling here; a second 500ms timer used to defeat the background budget.
            deadline.CancelAfter(LyricsCandidateSelectionProtocol.MaximumDecisionTime);
            using var registration = deadline.Token.Register(() =>
            { session.MarkCancelled(); _ = session.Child.TerminateAndWaitForExitAsync(); });
            try
            {
                MarkExecuting(session, "selection");
                // The native helper clears KV memory and resets the sampler per request.
                // This is the independent selector prompt, never translation output.
                var id = Guid.NewGuid().ToString("N");
                var request = JsonSerializer.Serialize(new { protocol = SelectionProtocolVersion, id, prompt },
                    new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                await session.Child.StandardInput!.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
                await session.Child.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                SelectionRequestSent?.Invoke();
                using var response = await ReadFrameAsync(session.Child.StandardOutput, deadline.Token).ConfigureAwait(false);
                var root = response.RootElement;
                if (root.GetProperty("protocol").GetInt32() != SelectionProtocolVersion || root.GetProperty("id").GetString() != id ||
                    !root.GetProperty("complete").GetBoolean()) throw new InvalidDataException("Invalid selection frame.");
                var output = LlamaCompletionRunner.RemoveRuntimeTerminator(root.GetProperty("text").GetString() ?? string.Empty);
                if (Encoding.UTF8.GetByteCount(output) > LyricsCandidateSelectionProtocol.MaximumOutputBytes)
                    throw new InvalidDataException("Selection output exceeds budget.");
                deadline.Token.ThrowIfCancellationRequested();
                RecordExecution(session, "selection");
                _ = ReleaseWhenIdleAsync(++_generation);
                return output;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                await ObserveNaturalExitAsync(session, error).ConfigureAwait(false);
                try { await StopSessionAsync().ConfigureAwait(false); }
                finally
                {
                    if (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                        RecordFailure(modelHash, session.Gpu, session, error, deadline.IsCancellationRequested, false);
                }
                throw;
            }
            finally { EndExecution(); }
        }
        finally { _operation.Release(); }
    }

    private async Task StartSessionAsync(string modelPath, AiLyricsModelDescriptor model, bool gpu, CancellationToken token, bool nonblocking = false)
    {
        await _cleanup.WaitAsync(token).ConfigureAwait(false);
        if (gpu && _automaticCuda is not null)
        {
            var preferCuda = _options.Backend != LyricsGpuBackend.Vulkan;
            bool? compatibleDriver = preferCuda ? CudaDriverAvailability.IsCompatible() : null;
            bool? cudaInstalled = preferCuda ? _automaticCuda.HasInstalledFiles : null;
            _gpuBackend = compatibleDriver == true && cudaInstalled == true ? "cuda" : "vulkan";
            _logger.LogInformation("Local lyrics runtime selected: preference={Preference}; backend={Backend}; compatibleNvidiaDriver={CompatibleDriver}; cudaInstalled={CudaInstalled}.",
                _options.Backend, _gpuBackend, compatibleDriver, cudaInstalled);
        }
        CudaLyricsComponentLease? componentLease = null;
        try
        {
        string executable;
        using (var resolve = PlainLyricsMetrics.Measure(PlainLyricsMetrics.Stage.RuntimeResolve, token))
        {
            if (gpu && _gpuBackend == "cuda" && (_openCudaLease is not null || _automaticCuda is not null))
            {
                componentLease = await (_automaticCuda is not null
                    ? _automaticCuda.OpenWorkerLeaseAsync(token) : _openCudaLease!(token)).ConfigureAwait(false);
                executable = componentLease.ExecutablePath;
            }
            else executable = await _resolve(gpu, token).ConfigureAwait(false);
            resolve.Complete();
        }
        if (nonblocking)
        {
            if (Volatile.Read(ref _translationWaiters) != 0 || !LocalInferenceProcess.InferenceGate.Wait(0))
                throw new IOException("Inference is busy.");
        }
        else await WaitForGateAsync(LocalInferenceProcess.InferenceGate, token, PlainLyricsMetrics.Stage.NativeQueue).ConfigureAwait(false);
        var gateTransferred = false;
        try
        {
            using var load = PlainLyricsMetrics.Measure(PlainLyricsMetrics.Stage.LoadReady, token);
            var start = new ProcessStartInfo(Path.GetFullPath(executable))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in BuildArguments(modelPath, gpu, model.Sha256, _gpuBackend)) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(key =>
                key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("VULKAN_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CUDA_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("HIP_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["OMP_NUM_THREADS"] = "4";
            start.Environment["OMP_THREAD_LIMIT"] = "4";
            var actualBackend = gpu ? _gpuBackend : "cpu";
            var memoryBudget = ResidentInferenceMemoryPolicy.MaximumBytes(model.Sha256, actualBackend);
            token.ThrowIfCancellationRequested();
            // The old worker has fully exited and this owner holds the global gate.
            // GPU fallback gets a fresh reading here; an existing resident is reused
            // without pretending that its already allocated weights are still free RAM.
            if (!gpu || actualBackend == "cuda" && model == AiLyricsModelCatalog.ExperimentalPlain)
                CpuInferenceMemoryPolicy.EnsureAvailable(memoryBudget, _readMemorySnapshot);
            token.ThrowIfCancellationRequested();
            _logger.LogInformation("Local lyrics worker launch: backend={Backend}; model={ModelId}; hostMemoryLimitBytes={Limit}; systemReserveBytes={Reserve}.",
                actualBackend, model.Id, memoryBudget, CpuInferenceMemoryPolicy.SystemReserveBytes);
            var child = LocalInferenceProcess.Start(start, memoryBudget, retainStandardInput: true);
            _session = new Session(child, Path.GetFullPath(modelPath), model.Sha256, memoryBudget, gpu)
            { ComponentLease = componentLease };
            componentLease = null;
            gateTransferred = true;
            var starting = _session;
            using var stop = token.Register(() =>
            { starting.MarkCancelled(); _ = child.TerminateAndWaitForExitAsync(); });
            using var ready = await ReadFrameAsync(child.StandardOutput, token).ConfigureAwait(false);
            if (ready.RootElement.GetProperty("protocol").GetInt32() != 1 || !ready.RootElement.GetProperty("ready").GetBoolean() ||
                ready.RootElement.GetProperty("backend").GetString() != (gpu ? _gpuBackend : "cpu"))
                throw new InvalidDataException("Unexpected resident runtime handshake.");
            if (gpu && _gpuBackend == "cuda" &&
                (!ready.RootElement.TryGetProperty("componentId", out var component) ||
                    component.GetString() != CudaLyricsRuntimePackage.RuntimeId))
                throw new InvalidDataException("Unexpected CUDA component handshake.");
            if (model == AiLyricsModelCatalog.ExperimentalLargePlain &&
                (!ready.RootElement.TryGetProperty("modelProfile", out var profile) || profile.GetString() != "hy-mt2-7b-q8"))
                throw new InvalidDataException("The resident worker did not confirm the selected 7B resource profile.");
            _session.Device = ready.RootElement.TryGetProperty("device", out var device) ? device.Clone() : null;
            _session.IsReady = true;
            _logger.LogInformation("Local lyrics runtime ready: backend={Backend}; model={ModelId}; protocol={Protocol}.",
                gpu ? _gpuBackend : "cpu", model.Id, ProtocolVersion);
            _session.SelectionSupported = ready.RootElement.TryGetProperty("selectionProtocol", out var selectionProtocol) &&
                selectionProtocol.ValueKind == JsonValueKind.Number && selectionProtocol.TryGetInt32(out var selectorVersion) &&
                selectorVersion == SelectionProtocolVersion;
            load.Complete();
        }
        finally { if (!gateTransferred) LocalInferenceProcess.InferenceGate.Release(); }
        }
        finally { componentLease?.Dispose(); }
    }

    private void RecordExecution(Session session, string operation)
    {
        var observed = new PlainLyricsExecutionStatus(session.Gpu ? _gpuBackend : "cpu", session.ModelSha256,
            _options.GpuEnabled, _options.Backend, _lastGpuSettingGeneration, _options.GpuEnabled && !session.Gpu,
            DateTimeOffset.UtcNow, operation);
        Volatile.Write(ref _lastExecution, observed);
        if (session.Gpu)
        {
            _gpuFailedModels.Remove(session.ModelSha256);
            Volatile.Write(ref _lastFailure, null);
        }
        _logger.LogInformation("Local lyrics inference completed: operation={Operation}; backend={Backend}; model={ModelId}; cpuFallback={CpuFallback}; preferenceGeneration={Generation}.",
            operation, observed.Backend, AiLyricsModelCatalog.FindSelectableByHash(observed.ModelSha256)?.Id,
            observed.UsedCpuFallback, observed.PreferenceGeneration);
        ExecutionStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginExecution(string modelHash, string operation)
    {
        Volatile.Write(ref _currentExecution, new(null, modelHash, _options.GpuEnabled,
            _options.Backend, _lastGpuSettingGeneration, PlainLyricsExecutionPhase.Starting, operation));
        ExecutionStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkExecuting(Session session, string operation)
    {
        // StartSession has validated the worker identity and ready frame. This is an active
        // request; only RecordExecution can assert a completed, valid native response.
        Volatile.Write(ref _currentExecution, new(session.Gpu ? _gpuBackend : "cpu", session.ModelSha256,
            _options.GpuEnabled, _options.Backend, _lastGpuSettingGeneration, PlainLyricsExecutionPhase.Executing, operation));
        ExecutionStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EndExecution()
    {
        if (Interlocked.Exchange(ref _currentExecution, null) is not null)
            ExecutionStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RecordFailure(string modelHash, bool gpu, Session? session, Exception error, bool timedOut, bool fallback)
    {
        var exitCode = session?.Child.ExitCode ?? ReadObservedExitCode(session);
        var hostStopped = session?.Child.TerminationRequested == true;
        var reason = timedOut ? "DeadlineExceeded" : session?.FailureCategory ??
            (hostStopped ? null : exitCode switch
            {
                65 => "RuntimeLoad", 66 => "GpuAdmission", 67 => "ModelLoadFailure", 71 => "DecodeFailure",
                unchecked((int)0xc0000135) => "DllMissing", unchecked((int)0xc000007b) => "DllInvalid",
                unchecked((int)0xc0000142) => "DllInitialization", _ => null,
            }) ?? error.GetType().Name;
        var failure = new PlainLyricsRuntimeFailure(_options.GpuEnabled ? _options.Backend.ToString() : "Cpu",
            gpu ? _gpuBackend : "cpu", modelHash, _options.GpuEnabled, _options.Backend, _lastGpuSettingGeneration,
            DateTimeOffset.UtcNow, reason, exitCode, hostStopped, fallback);
        // Preserve the original GPU cause when the CPU fallback also fails.
        if (gpu || LastFailure is null) Volatile.Write(ref _lastFailure, failure);
        _logger.LogWarning("Local lyrics runtime failed: requested={Requested}; attempted={Attempted}; model={ModelId}; utc={Time}; phase={Phase}; reason={Reason}; hresult={HResult}; exitCode={ExitCode}; hostTerminated={HostTerminated}; cpuFallback={CpuFallback}; generation={Generation}.",
            failure.RequestedBackend, failure.AttemptedBackend, AiLyricsModelCatalog.FindSelectableByHash(modelHash)?.Id,
            failure.OccurredAt, session?.IsReady == true ? "inference" : "startup", reason, error.HResult,
            exitCode, hostStopped, fallback, failure.PreferenceGeneration);
        ExecutionStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int? ReadObservedExitCode(Session? session)
    {
        try { return session?.Child.Process.HasExited == true ? session.Child.Process.ExitCode : null; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static async Task ObserveNaturalExitAsync(Session? session, Exception error)
    {
        if (session is null || error is not EndOfStreamException) return;
        // EOF can precede the OS exit signal slightly. Give a naturally exiting worker a
        // bounded opportunity to report its own code before cleanup requests termination.
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { await session.Child.Process.WaitForExitAsync(stop.Token).ConfigureAwait(false); }
        catch (Exception failure) when (failure is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private async Task StopSessionAsync()
    {
        EndExecution();
        ++_generation;
        var session = _session;
        if (session is not null)
        {
            _session = null;
            Interlocked.Exchange(ref session.StopRequested, 1);
            session.Cancellation.Dispose();
            var id = session.Child.Process.Id;
            _cleanup = CompleteSessionAsync(session);
            _ = ReleaseSessionGateAsync(_cleanup, session);
            await LocalInferenceProcess.WaitForCleanupAsync(_cleanup, id).ConfigureAwait(false);
        }
        else await _cleanup.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    private static async Task CompleteSessionAsync(Session session)
    {
        await session.Child.CompleteAsync(session.Errors, session.Memory).ConfigureAwait(false);
        session.ComponentLease?.Dispose();
    }

    private static async Task WaitForGateAsync(SemaphoreSlim gate, CancellationToken token, PlainLyricsMetrics.Stage stage)
    {
        using var measured = PlainLyricsMetrics.Measure(stage, token);
        await gate.WaitAsync(token).ConfigureAwait(false);
        measured.Complete();
    }

    private static async Task ReleaseSessionGateAsync(Task cleanup, Session session)
    {
        // Preserve the existing fail-closed gate release and retained continuation. Measure only
        // confirmed cleanup + release, including a completion after the foreground timeout.
        await LlamaCompletionRunner.ReleaseGateAfterCleanupAsync(cleanup, LocalInferenceProcess.InferenceGate).ConfigureAwait(false);
        var started = Interlocked.Read(ref session.CancelledAt);
        if (cleanup.IsCompletedSuccessfully && started != 0)
            PlainLyricsMetrics.Record(PlainLyricsMetrics.Stage.CancellationRelease, started, PlainLyricsMetrics.Outcome.Success);
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
        throw new EndOfStreamException("Resident inference exited before completing its response.");
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
        internal JsonElement? Device { get; set; }
        internal CudaLyricsComponentLease? ComponentLease { get; init; }
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
        internal long CancelledAt;
        internal void MarkCancelled()
        {
            Interlocked.CompareExchange(ref CancelledAt, Stopwatch.GetTimestamp(), 0);
            Interlocked.Exchange(ref StopRequested, 1);
        }
        internal bool SelectionSupported;
        internal bool IsReady;
        private string? _failureCategory;
        internal string? FailureCategory => Volatile.Read(ref _failureCategory);
        internal Task Errors { get; }
        internal Task Memory { get; }
        private async Task DrainErrorsAsync(StreamReader reader)
        {
            var buffer = new char[2048];
            var boundary = string.Empty;
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                // Only fixed categories survive; never persist raw stderr, paths, prompts or lyrics.
                var chunk = boundary + new string(buffer, 0, read);
                var category = ClassifyNativeFailure(chunk);
                if (category is not null) Volatile.Write(ref _failureCategory, category);
                boundary = chunk[^Math.Min(256, chunk.Length)..];
            }
        }
        private static string? ClassifyNativeFailure(string text)
        {
            bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
            if (Has("out of memory") || Has("CUDA_ERROR_OUT_OF_MEMORY")) return "OutOfMemory";
            if (Has("insufficient VRAM") || Has("not enough VRAM")) return "VramBudget";
            if (Has("CUDA_ERROR_UNSUPPORTED_PTX_VERSION") || Has("no kernel image") || Has("invalid device function")) return "CudaArchitecture";
            if (Has("no CUDA device") || Has("CUDA_ERROR_NO_DEVICE") || Has("CUDA_ERROR_INSUFFICIENT_DRIVER")) return "CudaDeviceOrDriver";
            if (Has("CUBLAS_STATUS_") || Has("cublas error")) return "CublasFailure";
            if (Has("failed to load model") || Has("error loading model")) return "ModelLoadFailure";
            return null;
        }
        private async Task MonitorAsync(LocalInferenceProcess child, long memoryBudget)
        {
            while (!child.Process.HasExited)
            {
                child.Process.Refresh();
                if (child.Process.WorkingSet64 > memoryBudget)
                {
                    Volatile.Write(ref _failureCategory, "HostMemoryBudgetExceeded");
                    Interlocked.Exchange(ref StopRequested, 1);
                    await child.TerminateAndWaitForExitAsync().ConfigureAwait(false); return;
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }
}
