using System.Diagnostics;
using System.Text;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Runs a pre-verified local runtime with bounded output and cancellation. No server or tools are exposed.</summary>
public sealed class LlamaCompletionRunner : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private const int MaximumOutputCharacters = 65_536;
    private const string Schema = "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"},\"text\":{\"type\":\"string\"}},\"required\":[\"id\",\"text\"],\"additionalProperties\":false}}";

    public async Task<string> RunAsync(string executablePath, string modelPath, string prompt, string stagingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Encoding.UTF8.GetByteCount(prompt) > 80_000) throw new InvalidDataException("Prompt exceeds budget.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            Directory.CreateDirectory(stagingDirectory);
            var promptPath = Path.Combine(stagingDirectory, $"lyrics-{Guid.NewGuid():N}.txt");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var promptFile = new FileStream(promptPath, options);
            await promptFile.WriteAsync(Encoding.UTF8.GetBytes(prompt), deadline.Token).ConfigureAwait(false);
            await promptFile.FlushAsync(deadline.Token).ConfigureAwait(false);
            var start = new ProcessStartInfo(Path.GetFullPath(executablePath))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-m", Path.GetFullPath(modelPath), "-f", promptPath, "--offline", "--jinja",
                "--single-turn", "--no-display-prompt", "--simple-io", "--no-context-shift", "--reasoning", "off", "-t", "4", "-c", "4096", "-n", "2048", "--temp", "0.1", "-j", Schema })
                start.ArgumentList.Add(argument);
            // Prevent ambient runtime flags from overriding these controlled model and network options.
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("Local inference failed to start.");
            process.StandardInput.Close();
            using var stop = deadline.Token.Register(() => Stop(process));
            var output = ReadBoundedAsync(process.StandardOutput, process, deadline.Token);
            var errors = DrainAsync(process.StandardError, deadline.Token);
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
        finally { _gate.Release(); }
    }

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

    // Runtime watchdog, not a claim of OS sandboxing. Native Windows hard limits are a separate release gate.
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
