using System.Diagnostics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Owns one inference process and its redirected streams.</summary>
internal sealed class LocalInferenceProcess : IDisposable
{
    internal static readonly SemaphoreSlim InferenceGate = new(1, 1);
    private IDisposable? _limits;
    private Task? _exit;
    private Task? _cleanup;
    private bool _disposed;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    internal LocalInferenceProcess(Process process, StreamReader output, StreamReader errors,
        StreamWriter? input = null, IDisposable? limits = null)
    {
        Process = process;
        StandardOutput = output;
        StandardError = errors;
        StandardInput = input;
        _limits = limits;
    }

    internal Process Process { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }
    internal StreamWriter? StandardInput { get; }

    internal static LocalInferenceProcess Start(ProcessStartInfo start, long memoryLimitBytes = WindowsInferenceProcess.MaximumMemoryBytes,
        bool retainStandardInput = false)
    {
        if (OperatingSystem.IsWindows()) return WindowsInferenceProcess.Start(start, memoryLimitBytes, retainStandardInput);
        // Development/test support only. Production is Windows and always requires the native limits.
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("Local inference failed to start.");
            if (!retainStandardInput) process.StandardInput.Close();
            return new LocalInferenceProcess(process, process.StandardOutput, process.StandardError,
                retainStandardInput ? process.StandardInput : null);
        }
        catch { process.Dispose(); throw; }
    }

    internal Task TerminateAndWaitForExitAsync() => _exit ??= TerminateCoreAsync();

    private async Task TerminateCoreAsync()
    {
        Terminate();
        // Kill and KILL_ON_JOB_CLOSE only request termination. A cancelled caller must still
        // await OS exit before the runtime image/model can be deleted or the gate released.
        // The mandatory Windows job permits just this one process and forbids breakaway.
        if (OperatingSystem.IsWindows())
            await WindowsInferenceProcess.WaitForExitSignalAsync(Process.SafeHandle).ConfigureAwait(false);
        else
            await Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // The task retains this owner and its streams until exit and all I/O have settled, even
    // when the bounded caller wait expires. It is also the inference gate's release barrier.
    internal Task CompleteAsync(params Task[] operations) => _cleanup ??= CompleteCoreAsync(operations);

    private async Task CompleteCoreAsync(Task[] operations)
    {
        await TerminateAndWaitForExitAsync().ConfigureAwait(false);
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch (Exception)
        {
            // The invoking inference owns these operation results and preserves its primary
            // failure. Here only settlement matters: a reader/watchdog fault does not undo
            // confirmed OS exit or make successfully released handles unsafe to reuse.
        }
        finally
        {
            _disposed = true;
            StandardOutput.Dispose();
            StandardError.Dispose();
            StandardInput?.Dispose();
            Process.Dispose();
        }
    }

    internal static async Task WaitForCleanupAsync(Task cleanup, int processId, TimeSpan? timeout = null)
    {
        var budget = timeout ?? ShutdownTimeout;
        try { await cleanup.WaitAsync(budget).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Local inference process {processId} cleanup exceeded {budget.TotalSeconds} seconds; process ownership and exit observation remain active.", error);
        }
    }

    private void Terminate()
    {
        // Closing the only job handle kills the child even if normal shutdown fails.
        Interlocked.Exchange(ref _limits, null)?.Dispose();
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        var processId = Process.Id;
        WaitForCleanupAsync(CompleteAsync(), processId).GetAwaiter().GetResult();
    }
}
