using System.Text;
using System.Text.Json;
using DropSpace.Core.Dlc;

namespace DropSpace.Infrastructure.Dlc;

/// <summary>One owned process/session, one bounded outstanding request. This is fault isolation, not an OS sandbox.</summary>
public sealed class ModuleWorkerClient : IAsyncDisposable
{
    private readonly ModuleWorkerProcess _process;
    private readonly ModuleProcessJob _job;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Task _stderr;
    private long _nextId;
    private int _stopping, _activeRequests, _disposed, _asyncResourcesDisposed;
    private Task<bool>? _stopTask;
    private readonly object _stopSync = new();
    public bool IsAlive { get { try { return Volatile.Read(ref _stopping) == 0 && !_process.HasExited; } catch (InvalidOperationException) { return false; } } }
    public event EventHandler? Exited;

    private ModuleWorkerClient(string executable, string dataDirectory, int dataVersion)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Official modules require Windows.");
        _job = new();
        try { _process = ModuleWorkerProcess.Start(executable, dataDirectory, _session, dataVersion, _job); }
        catch { _job.Dispose(); throw; }
        _process.Exited += OnExited;
        _stderr = DrainStderrAsync();
    }

    public static async Task<ModuleWorkerClient> StartAsync(string executable, string dataDirectory, ModuleManifest manifest, CancellationToken token)
    {
        var worker = new ModuleWorkerClient(executable, dataDirectory, manifest.Data.Version);
        try
        {
            var result = await worker.RequestAsync("hello", JsonSerializer.SerializeToElement(new
            {
                moduleId = manifest.Id, moduleVersion = manifest.Version, hostInterface = ModuleContract.HostInterface,
                protocol = ModuleContract.Protocol, dataVersion = manifest.Data.Version,
                capabilities = manifest.RequiredCapabilities.Concat(manifest.OptionalCapabilities)
                    .Where(c => ModuleContract.HasCapability(manifest, c.Id)).ToArray(),
            }), token).ConfigureAwait(false);
            if (result.Result.GetProperty("moduleId").GetString() != manifest.Id ||
                result.Result.GetProperty("moduleVersion").GetString() != manifest.Version ||
                result.Result.GetProperty("protocol").GetInt32() != ModuleContract.Protocol ||
                result.Result.GetProperty("dataVersion").GetInt32() != manifest.Data.Version)
                throw new InvalidDataException("ModuleHandshakeMismatch");
            return worker;
        }
        catch { await worker.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<ModuleReply> RequestAsync(string method, JsonElement payload, CancellationToken token)
    {
        var entered = false;
        lock (_stopSync)
        {
            if (!IsAlive || Volatile.Read(ref _disposed) != 0) throw new IOException("ModuleNotRunning");
            Interlocked.Increment(ref _activeRequests);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            if (!IsAlive) throw new IOException("ModuleNotRunning");
            return await ExchangeAsync(method, payload, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Retire the entire session. A late result can never update a later generation.
            // A request canceled before admission has sent nothing and cannot leave a late result.
            if (entered) RetireSession();
            throw new OperationCanceledException(token.IsCancellationRequested ? "ModuleCanceled" : "ModuleTimeout", token);
        }
        catch { if (entered) RetireSession(); throw; }
        finally
        {
            if (entered) _requests.Release();
            Interlocked.Decrement(ref _activeRequests);
            DisposeAsyncResourcesWhenIdle();
        }
    }

    private async Task<ModuleReply> ExchangeAsync(string method, JsonElement payload, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = JsonSerializer.Serialize(new ModuleEnvelope(ModuleContract.Protocol, _session, id, method, payload));
        if (Encoding.UTF8.GetByteCount(request) > ModuleContract.MaximumMessageBytes) throw new InvalidDataException("ModuleMessageTooLarge");
        await AwaitPipeAsync(_process.StandardInput.WriteLineAsync(request.AsMemory(), token), token).ConfigureAwait(false);
        await AwaitPipeAsync(_process.StandardInput.FlushAsync(token), token).ConfigureAwait(false);
        var responseBytes = new byte[ModuleContract.MaximumMessageBytes];
        var count = 0;
        var single = new byte[1];
        var output = _process.StandardOutput.BaseStream;
        // Bounded before decoding; ReadLineAsync would permit an unbounded allocation.
        while (true)
        {
            var read = await AwaitPipeAsync(output.ReadAsync(single, token).AsTask(), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("ModuleDisconnected");
            if (single[0] == (byte)'\n') break;
            if (count == responseBytes.Length) throw new InvalidDataException("ModuleMessageTooLarge");
            responseBytes[count++] = single[0];
        }
        var reply = JsonSerializer.Deserialize<ModuleEnvelope>(responseBytes.AsSpan(0, count)) ?? throw new InvalidDataException("ModuleProtocolInvalid");
        if (reply.Protocol != ModuleContract.Protocol || reply.Session != _session || reply.Id != id || reply.Method != "result")
            throw new InvalidDataException("ModuleResultExpired");
        if (reply.ErrorCode is not null) throw new IOException("ModuleError:" + reply.ErrorCode);
        return reply.Payload.Deserialize<ModuleReply>() ?? throw new InvalidDataException("ModuleProtocolInvalid");
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            var buffer = new char[1024]; var total = 0;
            while (await _process.StandardError.ReadAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false) is var count && count > 0)
            { total += count; if (total > 1024 * 1024) { TryKill(); break; } }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
    private void OnExited(object? sender, EventArgs args) => Exited?.Invoke(this, EventArgs.Empty);
    private void TryKill()
    {
        try { _job.Terminate(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
    private void RetireSession()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _lifetime.Cancel();
        TryKill();
    }
    private async Task AwaitPipeAsync(Task operation, CancellationToken token)
    {
        try { await operation.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            RetireSession();
            await SettlePipeAsync(operation).ConfigureAwait(false);
            throw;
        }
    }
    private async Task<int> AwaitPipeAsync(Task<int> operation, CancellationToken token)
    {
        try { return await operation.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            RetireSession();
            await SettlePipeAsync(operation).ConfigureAwait(false);
            throw;
        }
    }
    private static async Task SettlePipeAsync(Task operation)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        catch (TimeoutException)
        {
            // The session is already retired/killed. Observe any eventual completion without
            // retaining a module/runtime owner or allowing another exchange on this stream.
            _ = operation.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    public Task<bool> StopAsync()
    {
        lock (_stopSync)
        {
            if (_stopTask is { IsCompletedSuccessfully: true, Result: false })
                _stopTask = null; // later physical exit must be confirmable by cleanup retry
            return _stopTask ??= StopCoreAsync();
        }
    }
    private async Task<bool> StopCoreAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _lifetime.Cancel();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var entered = false;
        try
        {
            entered = await _requests.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            if (entered && !_process.HasExited)
                await ExchangeAsync("stop", JsonSerializer.SerializeToElement(new { }), deadline.Token).ConfigureAwait(false);
            await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException or InvalidOperationException) { }
        finally { if (entered) _requests.Release(); }
        TryKill();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        return _process.HasExited && await _job.WaitForEmptyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!await StopAsync().ConfigureAwait(false)) return; // retain process handle until exit can actually be confirmed
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _process.Exited -= OnExited;
        try { await _stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (TimeoutException) { }
        _process.Dispose();
        _job.Dispose();
        DisposeAsyncResourcesWhenIdle();
    }
    private void DisposeAsyncResourcesWhenIdle()
    {
        if (Volatile.Read(ref _disposed) != 0 && Volatile.Read(ref _activeRequests) == 0 &&
            Interlocked.Exchange(ref _asyncResourcesDisposed, 1) == 0)
        { _requests.Dispose(); _lifetime.Dispose(); }
    }
}
