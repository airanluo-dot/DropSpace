using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Translation-shaped view over the unified lyrics cache.</summary>
public sealed class AiLyricsCache
{
    private readonly LyricsCache _cache;
    public AiLyricsCache(string root) : this(new LyricsCache(root)) { }
    public AiLyricsCache(LyricsCache cache) => _cache = cache;
    public long Generation => _cache.Generation;
    public long ExecutionGeneration => _cache.ExecutionGeneration;
    public bool AllowsExecution(long generation) => _cache.AllowsExecution(generation);

    public Task<string?> ReadAsync(string key, CancellationToken token)
    {
        ValidateKey(key);
        return _cache.ReadAsync("ai", key, LyricsTranslationOutput.MaximumOutputBytes, token);
    }

    public Task WriteAsync(string key, string validatedJson, CancellationToken token) =>
        WriteAsync(key, validatedJson, Generation, token);

    public Task WriteAsync(string key, string validatedJson, long generation, CancellationToken token, Func<bool>? isCurrent = null)
    {
        ValidateKey(key);
        return _cache.WriteAsync("ai", key, validatedJson, LyricsTranslationOutput.MaximumOutputBytes, generation, token, isCurrent);
    }

    public Task ClearAsync(CancellationToken token) => _cache.ClearAsync(token);

    /// <summary>Retire incompatible, app-owned pre-unified entries; never import their translations.</summary>
    public static Task RemoveLegacyAsync(string applicationRoot, CancellationToken token) => Task.Run(() =>
    {
        var root = Path.Combine(applicationRoot, "AiLyrics", "Cache");
        if (!Directory.Exists(root)) return;
        ReparseSafePathPolicy.ResolveExistingContainedPath(applicationRoot, root);
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var ownedJson = name.Length == 69 && name.EndsWith(".json", StringComparison.Ordinal) && name[..64].All(Uri.IsHexDigit);
            var ownedTemporary = name.Length == 101 && name[64] == '.' && name.EndsWith(".tmp", StringComparison.Ordinal) &&
                name[..64].All(Uri.IsHexDigit) && Guid.TryParseExact(name.Substring(65, 32), "N", out _);
            if (!ownedJson && !ownedTemporary) continue;
            File.Delete(ReparseSafePathPolicy.ResolveExistingContainedPath(applicationRoot, path));
        }
    }, token);

    private static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid cache key.", nameof(key));
    }
}
