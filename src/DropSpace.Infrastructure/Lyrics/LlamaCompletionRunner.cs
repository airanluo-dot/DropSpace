using System.Diagnostics;
using System.Text;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Runs a pre-verified local runtime with bounded output and cancellation. No server or tools are exposed.</summary>
public sealed class LlamaCompletionRunner : IDisposable
{
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _cleanupSync = new();
    private Task _pendingCleanup = Task.CompletedTask;
    private bool _disposed;
    private const int MaximumOutputCharacters = 65_536;
    private const string Schema = "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"},\"text\":{\"type\":\"string\"}},\"required\":[\"id\",\"text\"],\"additionalProperties\":false}}";

    public async Task<string> RunAsync(string executablePath, string modelPath, string prompt, string stagingDirectory,
        CancellationToken cancellationToken, string? verifiedModelSha256 = null, IReadOnlyList<int>? expectedLineIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var outputSchema = expectedLineIds is null ? Schema : DropSpace.Core.Lyrics.LyricsTranslationPrompt.OutputSchema(expectedLineIds);
        if (Encoding.UTF8.GetByteCount(prompt) > 80_000) throw new InvalidDataException("Prompt exceeds budget.");
        var memoryBudget = MemoryBudgetFor(verifiedModelSha256);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        // An unresolved previous teardown keeps the gate closed, but must not leave a
        // caller without an external cancellation token waiting indefinitely.
        deadline.CancelAfter(TimeSpan.FromSeconds(70));
        await InferenceGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        Task? cleanup = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var promptPath = ReparseSafePathPolicy.PrepareContainedFileDestination(stagingDirectory, $"lyrics-{Guid.NewGuid():N}.txt");
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    Options = FileOptions.Asynchronous,
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                // Close before launch: native std::ifstream does not request FILE_SHARE_DELETE.
                await using (var promptFile = new FileStream(promptPath, options))
                {
                    await promptFile.WriteAsync(Encoding.UTF8.GetBytes(prompt), deadline.Token).ConfigureAwait(false);
                    await promptFile.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                var start = new ProcessStartInfo(Path.GetFullPath(executablePath))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var argument in new[] { "-m", Path.GetFullPath(modelPath), "-f", promptPath, "--offline", "--no-escape", "--jinja",
                    "--single-turn", "--load-mode", "none", "--no-display-prompt", "--simple-io", "--no-context-shift", "--reasoning", "off", "-t", "4", "-tb", "4", "-ngl", "0", "-c", "4096", "-n", "2048", "--temp", "0.1", "-j", outputSchema })
                    start.ArgumentList.Add(argument);
                foreach (var argument in ModelCompatibilityArguments(verifiedModelSha256)) start.ArgumentList.Add(argument);
                // Prevent ambient runtime flags from overriding these controlled model and network options.
                foreach (var key in start.Environment.Keys.Where(key => (key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase))).ToArray())
                    start.Environment.Remove(key);
                start.Environment["OMP_NUM_THREADS"] = "4";
                start.Environment["OMP_THREAD_LIMIT"] = "4";
                var child = LocalInferenceProcess.Start(start, memoryBudget);
                var process = child.Process;
                using var stop = deadline.Token.Register(() => Stop(process));
                var output = ReadBoundedAsync(child.StandardOutput, process, deadline.Token);
                var errors = DrainAsync(child.StandardError, deadline.Token);
                var memory = MonitorMemoryAsync(process, memoryBudget, deadline.Token);
                Exception? primaryFailure = null;
                try
                {
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    var text = await output.ConfigureAwait(false);
                    await errors.ConfigureAwait(false);
                    await memory.ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new LocalInferenceExecutionException(process.ExitCode, tokenizer: false);
                    return RemoveRuntimeTerminator(text);
                }
                catch (Exception error) { primaryFailure = error; throw; }
                finally
                {
                    var processId = process.Id;
                    deadline.Cancel();
                    cleanup = child.CompleteAsync(output, errors, memory);
                    await WaitForCleanupPreservingFailureAsync(cleanup, processId, primaryFailure).ConfigureAwait(false);
                }
            }
            finally
            {
                cleanup = RemovePromptAfterCleanupAsync(cleanup, promptPath);
            }
        }
        finally
        {
            if (cleanup is null) InferenceGate.Release();
            else
            {
                TrackCleanup(cleanup);
                _ = ReleaseGateAfterCleanupAsync(cleanup, InferenceGate);
            }
        }
    }

    public async Task<int> CountTokensAsync(string tokenizerPath, string modelPath, string prompt,
        string stagingDirectory, CancellationToken token, string? verifiedModelSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Encoding.UTF8.GetByteCount(prompt) > 80_000) throw new InvalidDataException("Prompt exceeds budget.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await InferenceGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        Task? cleanup = null;
        string? promptPath = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            promptPath = ReparseSafePathPolicy.PrepareContainedFileDestination(stagingDirectory, $"tokenize-{Guid.NewGuid():N}.txt");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(promptPath, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(prompt), deadline.Token).ConfigureAwait(false);
                await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            var start = new ProcessStartInfo(Path.GetFullPath(tokenizerPath))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-m", Path.GetFullPath(modelPath), "-f", promptPath, "--offline", "--no-escape", "--ids", "--show-count" })
                start.ArgumentList.Add(arg);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            var memoryBudget = MemoryBudgetFor(verifiedModelSha256);
            var child = LocalInferenceProcess.Start(start, memoryBudget);
            using var stop = deadline.Token.Register(() => Stop(child.Process));
            var output = ReadBoundedAsync(child.StandardOutput, child.Process, deadline.Token, 1_048_576);
            var errors = DrainAsync(child.StandardError, deadline.Token);
            var memory = MonitorMemoryAsync(child.Process, memoryBudget, deadline.Token);
            Exception? primaryFailure = null;
            try
            {
                await child.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                await Task.WhenAll(errors, memory).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (child.Process.ExitCode != 0) throw new LocalInferenceExecutionException(child.Process.ExitCode, tokenizer: true);
                const string marker = "Total number of tokens:";
                var offset = text.LastIndexOf(marker, StringComparison.Ordinal);
                if (offset < 0 || !int.TryParse(text[(offset + marker.Length)..].Trim(),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) || count is < 1 or > 100_000)
                    throw new InvalidDataException("Invalid tokenizer result.");
                return count;
            }
            catch (Exception error) { primaryFailure = error; throw; }
            finally
            {
                var processId = child.Process.Id;
                deadline.Cancel();
                cleanup = child.CompleteAsync(output, errors, memory);
                await WaitForCleanupPreservingFailureAsync(cleanup, processId, primaryFailure).ConfigureAwait(false);
            }
        }
        finally
        {
            if (promptPath is not null) cleanup = RemovePromptAfterCleanupAsync(cleanup, promptPath);
            if (cleanup is null) InferenceGate.Release();
            else
            {
                TrackCleanup(cleanup);
                _ = ReleaseGateAfterCleanupAsync(cleanup, InferenceGate);
            }
        }
    }

    internal void TrackCleanup(Task cleanup)
    {
        lock (_cleanupSync)
            _pendingCleanup = _pendingCleanup.IsCompletedSuccessfully ? cleanup : Task.WhenAll(_pendingCleanup, cleanup);
    }

    /// <summary>Call after active work is stopped; a timeout never abandons native cleanup ownership.</summary>
    public Task DrainCleanupAsync(CancellationToken token) => DrainCleanupAsync(token, TimeSpan.FromSeconds(10));

    internal async Task DrainCleanupAsync(CancellationToken token, TimeSpan timeout)
    {
        Task cleanup;
        lock (_cleanupSync) cleanup = _pendingCleanup;
        try { await cleanup.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (TimeoutException error) when (!cleanup.IsCompleted)
        {
            throw new TimeoutException("Local inference cleanup is still pending; model/cache maintenance was not started.", error);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new IOException("Local inference cleanup could not be confirmed; model/cache maintenance was not started.", error);
        }
        lock (_cleanupSync)
            if (ReferenceEquals(cleanup, _pendingCleanup)) _pendingCleanup = Task.CompletedTask;
    }

    internal static async Task WaitForCleanupPreservingFailureAsync(Task cleanup, int processId,
        Exception? primaryFailure, TimeSpan? timeout = null)
    {
        try { await LocalInferenceProcess.WaitForCleanupAsync(cleanup, processId, timeout).ConfigureAwait(false); }
        catch (Exception error) when (primaryFailure is not null)
        {
            primaryFailure.Data["LocalInferenceShutdownFailure"] = error.ToString();
            Trace.TraceError("Local inference cleanup failed: {0}", error);
        }
    }

    internal static async Task ReleaseGateAfterCleanupAsync(Task cleanup, SemaphoreSlim gate)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception error)
        {
            // Fail closed if exit/cleanup cannot be established. Never overlap another
            // inference with a child whose resource ownership is unresolved.
            Trace.TraceError("Local inference gate remains held after cleanup failure: {0}", error);
            return;
        }
        gate.Release();
    }

    private static async Task RemovePromptAfterCleanupAsync(Task? cleanup, string promptPath)
    {
        // A teardown timeout leaves this continuation owning prompt removal until exit.
        if (cleanup is not null) await cleanup.ConfigureAwait(false);
        try { File.Delete(promptPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static long MemoryBudgetFor(string? modelSha256) =>
        string.Equals(modelSha256, DropSpace.Core.Lyrics.AiLyricsModelCatalog.Compact.Sha256, StringComparison.OrdinalIgnoreCase)
            ? 1536L * 1024 * 1024 : 3L * 1024 * 1024 * 1024;

    internal static IReadOnlyList<string> ModelCompatibilityArguments(string? verifiedModelSha256) =>
        string.Equals(verifiedModelSha256, DropSpace.Core.Lyrics.AiLyricsModelCatalog.Compact.Sha256, StringComparison.OrdinalIgnoreCase)
            ? new[] { "--override-kv", "tokenizer.ggml.eos_token_id=int:120020" }
            : Array.Empty<string>();

    public static string RemoveRuntimeTerminator(string output)
    {
        var text = output.Trim();
        const string marker = "[end of text]";
        return text.EndsWith(marker, StringComparison.Ordinal) ? text[..^marker.Length].TrimEnd() : text;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, Process process, CancellationToken token, int maximumCharacters = MaximumOutputCharacters)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > maximumCharacters)
            {
                Stop(process);
                throw new InvalidDataException("Local inference output exceeds budget.");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { }
    }

    // Working-set watchdog supplements the mandatory Windows committed-memory job limit.
    // Non-Windows development runs have this watchdog only.
    private static async Task MonitorMemoryAsync(Process process, long memoryBudget, CancellationToken token)
    {
        while (!process.HasExited)
        {
            try
            {
                process.Refresh();
                if (!process.HasExited && process.WorkingSet64 > memoryBudget)
                {
                    Stop(process);
                    throw new InvalidDataException("Local inference exceeded the memory budget.");
                }
            }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { return; }
            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // Do not dispose the gate while an in-flight call still owns its release.
    }
}
