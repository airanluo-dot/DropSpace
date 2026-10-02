using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>
/// The single, app-owned persistent store for provider documents and generated translations.
/// Entries are derived data. The directory must never be a model or user-LRC directory.
/// </summary>
public sealed class LyricsCache
{
    public const long DefaultMaximumBytes = 1L * 1024 * 1024 * 1024;
    public const long MinimumMaximumBytes = 100L * 1024 * 1024;
    public const long MaximumMaximumBytes = 5L * 1024 * 1024 * 1024;
    private const long MinimumFreeSpace = 16L * 1024 * 1024;
    private const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private const string Extension = ".lyrics-cache";
    private sealed class RootState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Generation;
        public long MaximumBytes;
    }
    private static readonly ConcurrentDictionary<string, RootState> Roots = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly Func<long> _quota;
    private readonly RootState _state;
    private readonly SemaphoreSlim _gate;

    public LyricsCache(string root, Func<long>? quota = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _quota = quota ?? (() => DefaultMaximumBytes);
        _state = Roots.GetOrAdd(_root, _ => new());
        _gate = _state.Gate;
    }

    public long Generation => Interlocked.Read(ref _state.Generation);

    public Task SetMaximumBytesAsync(long bytes, CancellationToken token = default)
    {
        Interlocked.Exchange(ref _state.MaximumBytes, Math.Clamp(bytes, MinimumMaximumBytes, MaximumMaximumBytes));
        // Directory scans and eviction are synchronous filesystem work. Never run them
        // on the settings/UI caller, even when the semaphore is immediately available.
        return Task.Run(async () =>
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try { if (Directory.Exists(_root)) Trim(CurrentQuota()); }
            finally { _gate.Release(); }
        }, token);
    }

    private long CurrentQuota()
    {
        var configured = Interlocked.Read(ref _state.MaximumBytes);
        return configured == 0 ? Math.Clamp(_quota(), MinimumMaximumBytes, MaximumMaximumBytes) : configured;
    }

    public async Task<LyricsDocument?> ReadDocumentAsync(string identity, CancellationToken token)
    {
        var payload = await ReadAsync("source", identity, MaximumDocumentBytes, token).ConfigureAwait(false);
        if (payload is null) return null;
        try
        {
            var document = JsonSerializer.Deserialize<LyricsDocument>(payload, Json);
            return IsValidDocument(document) ? document : null;
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public Task WriteDocumentAsync(string identity, LyricsDocument document, long generation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WriteAsync("source", identity, JsonSerializer.Serialize(document, Json), MaximumDocumentBytes, generation, token);
    }

    internal async Task<string?> ReadAsync(string category, string identity, int maximumBytes, CancellationToken token)
    {
        var path = EntryPath(category, identity);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            ReparseSafePathPolicy.ResolveExistingContainedPath(_root, path);
            string payload;
            await using (var file = ReparseSafeFileOpen.OpenRead(path))
            {
                if (file.Length is <= 0 || file.Length > maximumBytes) return null;
                using var memory = new MemoryStream((int)file.Length);
                var buffer = new byte[8192];
                int count;
                while ((count = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (memory.Length + count > maximumBytes) return null;
                    memory.Write(buffer, 0, count);
                }
                payload = new UTF8Encoding(false, true).GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
            }
            // A failed optional access-time update must not turn good data into a miss.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return payload;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DecoderFallbackException)
        { return null; }
        finally { _gate.Release(); }
    }

    internal async Task WriteAsync(string category, string identity, string payload, int maximumBytes, long generation, CancellationToken token, Func<bool>? isCurrent = null)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("Lyrics cache entry exceeds its bounded size.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (generation != Generation || isCurrent?.Invoke() == false || bytes.LongLength > CurrentQuota()) return;
            if (!HasSafeFreeSpace(bytes.LongLength)) return;
            var final = EntryPath(category, identity);
            Directory.CreateDirectory(_root);
            temporary = ReparseSafePathPolicy.PrepareContainedFileDestination(_root,
                Path.GetFileName(final) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (generation != Generation) return;
            // Reclaim the complete post-replacement budget before publishing. If an
            // old entry cannot be evicted, the new entry must not increase disk usage.
            Trim(CurrentQuota() - bytes.LongLength, final, temporary);
            ReparseSafePathPolicy.RevalidatePreparedDestination(_root, final);
            // Recheck at publication, after potentially slow quota scans and file writes.
            token.ThrowIfCancellationRequested();
            if (generation != Generation || isCurrent?.Invoke() == false) return;
            File.Move(temporary, final, true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _state.Generation); // Fence writers before waiting for one already in progress.
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var path in OwnedFiles().Concat(TemporaryFiles()))
            {
                token.ThrowIfCancellationRequested();
                File.Delete(ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, Path.GetFileName(path)));
            }
        }
        finally { _gate.Release(); }
    }

    public void Clear()
    {
        Interlocked.Increment(ref _state.Generation);
        _gate.Wait();
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var path in OwnedFiles().Concat(TemporaryFiles()))
                File.Delete(ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, Path.GetFileName(path)));
        }
        finally { _gate.Release(); }
    }

    private string EntryPath(string category, string identity)
    {
        if (category is not ("source" or "ai")) throw new ArgumentException("Unknown lyrics cache category.", nameof(category));
        ArgumentNullException.ThrowIfNull(identity);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(category + "\n" + identity)));
        return Path.Combine(_root, category + "-" + hash + Extension);
    }

    private IEnumerable<string> OwnedFiles() => SafeFiles("*" + Extension)
        .Where(path => IsOwnedName(Path.GetFileName(path)));

    private IEnumerable<string> TemporaryFiles() => SafeFiles("*.tmp").Where(path =>
    {
        var name = Path.GetFileName(path);
        var suffixLength = 1 + 32 + 4;
        return name.Length > suffixLength && name[^(suffixLength)] == '.' &&
            Guid.TryParseExact(name.Substring(name.Length - 36, 32), "N", out _) &&
            IsOwnedName(name[..^suffixLength]);
    });

    private IEnumerable<string> SafeFiles(string pattern)
    {
        if (File.GetAttributes(_root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Lyrics cache must not traverse a reparse point.");
        return Directory.EnumerateFiles(_root, pattern,
            new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint });
    }

    private static bool IsOwnedName(string name)
    {
        var separator = name.IndexOf('-');
        return separator > 0 && separator + 1 + 64 + Extension.Length == name.Length &&
            name.EndsWith(Extension, StringComparison.Ordinal) &&
            name.AsSpan(separator + 1, 64).ToString().All(Uri.IsHexDigit) &&
            (name[..separator] is "source" or "ai");
    }

    private void Trim(long quota, string? replacing = null, string? preserveTemporary = null)
    {
        foreach (var path in TemporaryFiles().Where(path => path != preserveTemporary))
            File.Delete(ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, Path.GetFileName(path)));
        var files = OwnedFiles().Where(path => path != replacing).Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= quota) break;
            var length = file.Length;
            File.Delete(ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, file.Name));
            total -= length;
        }
    }

    private static bool IsValidDocument(LyricsDocument? document)
    {
        if (document is not { Lines.Count: > 0 and <= 10_000, Match: not null } || !Enum.IsDefined(document.Provider)) return false;
        var match = document.Match;
        if (match.Title is null || match.Artist is null || match.Album is null || match.TrackIdentity is null ||
            string.IsNullOrWhiteSpace(match.CandidateId) || !double.IsFinite(match.DurationSeconds) || !double.IsFinite(match.Score)) return false;
        TimeSpan? previous = null;
        foreach (var line in document.Lines)
        {
            if (line is null || line.Text is null || line.Words is null || line.End < line.Start ||
                (previous is { } start && line.Start < start) || !Enum.IsDefined(line.TranslationOrigin)) return false;
            previous = line.Start;
            TimeSpan? previousWord = null;
            foreach (var word in line.Words)
            {
                if (word is null || word.Text is null || word.End < word.Start ||
                    (previousWord is { } wordStart && word.Start < wordStart)) return false;
                previousWord = word.Start;
            }
        }
        return true;
    }

    private bool HasSafeFreeSpace(long writeBytes)
    {
        try
        {
            var root = Path.GetPathRoot(_root);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).AvailableFreeSpace - writeBytes >= MinimumFreeSpace;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }
}
