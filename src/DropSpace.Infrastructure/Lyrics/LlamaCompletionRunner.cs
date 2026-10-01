using System.Diagnostics;
using System.Text;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Runs a pre-verified local runtime with bounded output and cancellation. No server or tools are exposed.</summary>
public sealed class LlamaCompletionRunner : IDisposable
{
    private static readonly SemaphoreSlim InferenceGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private const int MaximumOutputCharacters = 65_536;
    private const string Schema = "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"},\"text\":{\"type\":\"string\"}},\"required\":[\"id\",\"text\"],\"additionalProperties\":false}}";

    public async Task<string> RunAsync(string executablePath, string modelPath, string prompt, string stagingDirectory,
        CancellationToken cancellationToken, string? verifiedModelSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Encoding.UTF8.GetByteCount(prompt) > 80_000) throw new InvalidDataException("Prompt exceeds budget.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await InferenceGate.WaitAsync(deadline.Token).ConfigureAwait(false);
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
                foreach (var argument in new[] { "-m", Path.GetFullPath(modelPath), "-f", promptPath, "--offline", "--jinja",
                    "--single-turn", "--load-mode", "none", "--no-display-prompt", "--simple-io", "--no-context-shift", "--reasoning", "off", "-t", "4", "-tb", "4", "-ngl", "0", "-c", "4096", "-n", "2048", "--temp", "0.1", "-j", Schema })
                    start.ArgumentList.Add(argument);
                foreach (var argument in ModelCompatibilityArguments(verifiedModelSha256)) start.ArgumentList.Add(argument);
                // Prevent ambient runtime flags from overriding these controlled model and network options.
                foreach (var key in start.Environment.Keys.Where(key => (key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase))).ToArray())
                    start.Environment.Remove(key);
                start.Environment["OMP_NUM_THREADS"] = "4";
                start.Environment["OMP_THREAD_LIMIT"] = "4";
                using var child = LocalInferenceProcess.Start(start);
                var process = child.Process;
                using var stop = deadline.Token.Register(() => Stop(process));
                var output = ReadBoundedAsync(child.StandardOutput, process, deadline.Token);
                var errors = DrainAsync(child.StandardError, deadline.Token);
                var memory = MonitorMemoryAsync(process, deadline.Token);
                try
                {
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    var text = await output.ConfigureAwait(false);
                    await errors.ConfigureAwait(false);
                    await memory.ConfigureAwait(false);
                    if (process.ExitCode != 0) throw new IOException("Local inference exited unsuccessfully.");
                    return RemoveRuntimeTerminator(text);
                }
                finally
                {
                    Stop(process);
                    try { await Task.WhenAll(output, errors, memory).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { }
                }
            }
            finally
            {
                // Best-effort removal after success, timeout, cancellation, or launch failure.
                try { File.Delete(promptPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally { InferenceGate.Release(); }
    }

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

    private static async Task<string> ReadBoundedAsync(StreamReader reader, Process process, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > MaximumOutputCharacters)
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
    private static async Task MonitorMemoryAsync(Process process, CancellationToken token)
    {
        while (!process.HasExited)
        {
            try
            {
                process.Refresh();
                if (!process.HasExited && process.WorkingSet64 > 3L * 1024 * 1024 * 1024)
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
