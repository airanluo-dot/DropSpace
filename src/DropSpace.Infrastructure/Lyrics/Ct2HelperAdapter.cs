using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

public enum Ct2TokenizerProtocol { ArgosSentencePiece, HelsinkiSentencePiece }
public enum Ct2DecoderProtocol { Argos, HelsinkiOpus }

public sealed record Ct2PackageIdentity(string EngineSha256, string ModelSha256, string TokenizerSha256,
    Ct2TokenizerProtocol Tokenizer, Ct2DecoderProtocol Decoder, string ManifestSha256 = "")
{
    public string CacheIdentity(string source, string target) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', "ct2-stdio-v1", EngineSha256, ModelSha256, TokenizerSha256,
            Tokenizer, Decoder, ManifestSha256, source, target))));
}

public sealed record Ct2RouteLeg(string Source, string Target, Ct2PackageIdentity Package);

public static class Ct2RoutePlanner
{
    public static IReadOnlyList<Ct2RouteLeg> Resolve(string source, string target,
        Func<string, string, Ct2PackageIdentity?> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        source = Normalize(source); target = Normalize(target);
        if (!IsSupported(source, target)) return [];
        var direct = packages(source, target);
        if (direct is not null) return [new(source, target, direct)];
        if (target == "zh" && source is "ja" or "ko")
        {
            var first = packages(source, "en");
            var second = packages("en", "zh");
            if (first is not null && second is not null) return [new(source, "en", first), new("en", "zh", second)];
        }
        return [];
    }

    internal static bool IsSupported(string source, string target) =>
        (target == "en" && source is "ja" or "ko" or "zh") || (target == "zh" && source is "en" or "ja" or "ko");

    private static string Normalize(string language) => (language ?? "").Split('-', '_')[0].ToLowerInvariant() switch
    { "ja" => "ja", "ko" => "ko", "zh" => "zh", "en" => "en", _ => "" };
}

public static class Ct2LyricsOutput
{
    public static LyricsDocument Apply(LyricsDocument original, IReadOnlyList<int> sourceIndices,
        IReadOnlyList<Ct2TranslationLine> translated, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(sourceIndices);
        ArgumentNullException.ThrowIfNull(translated);
        if (sourceIndices.Count != translated.Count || sourceIndices.Distinct().Count() != sourceIndices.Count) throw new InvalidDataException("Incomplete CT2 lyrics output.");
        var lines = original.Lines.ToArray();
        for (var i = 0; i < sourceIndices.Count; i++)
        {
            if (translated[i] is null || string.IsNullOrWhiteSpace(translated[i].Text) || translated[i].Id != sourceIndices[i] || sourceIndices[i] < 0 || sourceIndices[i] >= lines.Length)
                throw new InvalidDataException("CT2 lyrics IDs are missing, duplicated, or reordered.");
            lines[sourceIndices[i]] = lines[sourceIndices[i]] with
            {
                Secondary = translated[i].Text,
                TranslationOrigin = LyricsTranslationOrigin.LocalAi,
                TranslationLanguage = targetLanguage,
            };
        }
        return original with { Lines = lines };
    }
}

/// <summary>Hidden, app-private CT2 stdio boundary. Not selected by the production lyrics service.</summary>
public sealed class Ct2HelperAdapter : IDisposable
{
    private const int MaximumMessageBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    // Fail-closed native ownership must remain rooted even if a caller disposes its adapter.
    private static readonly System.Collections.Concurrent.ConcurrentBag<(Ct2PrivatePackage?, LocalInferenceProcess?)> UnresolvedOwners = [];
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _cleanupSync = new();
    private Task _pendingCleanup = Task.CompletedTask;
    private bool _disposed;

    public Ct2HelperAdapter(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<IReadOnlyList<Ct2TranslationLine>> TranslateAsync(Ct2PackageReference packageReference,
        string source, string target, IReadOnlyList<Ct2SourceLine> lines, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!Ct2RoutePlanner.IsSupported(source, target)) throw new InvalidDataException("Unsupported explicit CT2 route.");
        // Snapshot before any asynchronous work; caller mutation cannot alter the validation contract.
        var requested = lines.ToArray();
        if (requested.Length is < 1 or > 2048 || requested.Any(x => x is null || x.Id < 0 || !ValidText(x.Text)) ||
            requested.Select(x => x.Id).Distinct().Count() != requested.Length)
            throw new InvalidDataException("Invalid CT2 request lines or IDs.");
        TaskCompletionSource ownership;
        lock (_cleanupSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ownership = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingCleanup = _pendingCleanup.IsCompletedSuccessfully ? ownership.Task : Task.WhenAll(_pendingCleanup, ownership.Task);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(_timeout);
        var acquired = false;
        Task? cleanup = null;
        Ct2PrivatePackage? package = null;
        LocalInferenceProcess? processOwner = null;
        try
        {
            await LocalInferenceProcess.InferenceGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            deadline.Token.ThrowIfCancellationRequested();
            package = await Ct2PrivatePackage.OpenAsync(packageReference, source, target, deadline.Token).ConfigureAwait(false);
            var identity = package.Identity;
            var request = new Request(1, source, target, package.ModelDirectory, package.SourceTokenizer, package.TargetTokenizer,
                package.TargetPrefix, identity.Tokenizer.ToString(), identity.Decoder.ToString(), requested);
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("CT2 request exceeds the byte budget.");
            deadline.Token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(package.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
                WorkingDirectory = Path.GetDirectoryName(package.Executable)!,
            };
            start.ArgumentList.Add("--stdio-once");
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith("CT2_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OMP_", StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith("_PYI", StringComparison.OrdinalIgnoreCase) || key.StartsWith("LD_", StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("MKL_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OPENBLAS_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["OMP_NUM_THREADS"] = "4";
            start.Environment["OMP_THREAD_LIMIT"] = "4";
            start.Environment["MKL_NUM_THREADS"] = "4";
            start.Environment["OPENBLAS_NUM_THREADS"] = "4";
            start.Environment["CT2_INTER_THREADS"] = "1";
            start.Environment["CT2_INTRA_THREADS"] = "4";
            var child = processOwner = LocalInferenceProcess.Start(start, WindowsInferenceProcess.MaximumMemoryBytes, retainStandardInput: true);
            var processId = child.Process.Id; // CompleteAsync may synchronously dispose the Process.
            using var stop = deadline.Token.Register(() => Stop(child.Process));
            var output = ReadBoundedAsync(child.StandardOutput.BaseStream, child.Process, deadline.Token);
            var errors = ReadBoundedAsync(child.StandardError.BaseStream, child.Process, deadline.Token, retain: false);
            Exception? primary = null;
            try
            {
                await child.StandardInput!.BaseStream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                await child.StandardInput.BaseStream.WriteAsync("\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                await child.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                child.StandardInput.Close();
                await child.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                var result = await output.ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (child.Process.ExitCode != 0) throw new LocalInferenceExecutionException(child.Process.ExitCode, false);
                var translated = ParseResponse(requested, result);
                deadline.Token.ThrowIfCancellationRequested();
                return translated;
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                deadline.Cancel();
                cleanup = child.CompleteAsync(output, errors);
                await LlamaCompletionRunner.WaitForCleanupPreservingFailureAsync(cleanup, processId, primary).ConfigureAwait(false);
            }
        }
        finally
        {
            // Continuation retains package read locks and the shared inference gate through actual OS exit.
            _ = FinishOwnershipAsync(cleanup, package, processOwner, acquired, ownership);
        }
    }

    /// <summary>Stop new admissions and cancel active work before maintenance; this observes its actual exit barrier.</summary>
    public async Task DrainCleanupAsync(CancellationToken token)
    {
        Task cleanup; lock (_cleanupSync) cleanup = _pendingCleanup;
        await cleanup.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
    }

    private static async Task FinishOwnershipAsync(Task? cleanup, Ct2PrivatePackage? package, LocalInferenceProcess? processOwner, bool acquired, TaskCompletionSource ownership)
    {
        try
        {
            if (cleanup is not null) await cleanup.ConfigureAwait(false);
            package?.Dispose();
            if (acquired) LocalInferenceProcess.InferenceGate.Release();
            ownership.TrySetResult();
        }
        catch (Exception error)
        {
            // Do not release leases or the gate if native cleanup cannot be confirmed.
            UnresolvedOwners.Add((package, processOwner));
            ownership.TrySetException(error);
            Trace.TraceError("CT2 process cleanup remains unresolved: {0}", error);
        }
    }

    internal static IReadOnlyList<Ct2TranslationLine> ParseResponse(IReadOnlyList<Ct2SourceLine> requested, byte[] bytes)
    {
        using var document = Ct2Protocol.Parse(bytes);
        var response = document.RootElement;
        Ct2Protocol.Keys(response, "version", "lines");
        var lines = response.GetProperty("lines");
        if (Ct2Protocol.Int(response, "version") != 1 || lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() != requested.Count)
            throw new InvalidDataException("Incomplete CT2 response.");
        var result = new List<Ct2TranslationLine>();
        foreach (var line in lines.EnumerateArray())
        {
            Ct2Protocol.Keys(line, "id", "text");
            var id = Ct2Protocol.Int(line, "id");
            var text = Ct2Protocol.Text(line, "text");
            if (id != requested[result.Count].Id || !ValidText(text))
                throw new InvalidDataException("Invalid CT2 response IDs or text.");
            result.Add(new(id, text));
        }
        return result;
    }

    private static bool ValidText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => c < 32 && c != '\t')) return false;
        try { return Utf8.GetByteCount(text) <= 4096; }
        catch (EncoderFallbackException) { return false; }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, Process process, CancellationToken token, bool retain = true)
    {
        using var result = new MemoryStream();
        var total = 0; var buffer = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaximumMessageBytes) { Stop(process); throw new InvalidDataException("CT2 stream exceeds the byte budget."); }
            if (retain) result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        lock (_cleanupSync) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel();
    }

    private sealed record Request(int Version, string Source, string Target, string ModelDirectory,
        string SourceTokenizer, string TargetTokenizer, string? TargetPrefix, string TokenizerProtocol,
        string DecoderProtocol, IReadOnlyList<Ct2SourceLine> Lines);
}

public sealed record Ct2SourceLine(int Id, string Text);
public sealed record Ct2TranslationLine(int Id, string Text);
