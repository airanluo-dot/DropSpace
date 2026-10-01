using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class AiLyricsCache(string root)
{
    private const long MaximumBytes = 100L * 1024 * 1024;

    public async Task<string?> ReadAsync(string key, CancellationToken token)
    {
        ValidateKey(key);
        var path = Path.Combine(root, key + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            ReparseSafePathPolicy.ResolveExistingContainedPath(root, path);
            if (new FileInfo(path).Length > LyricsTranslationOutput.MaximumOutputBytes) return null;
            string result;
            await using (var file = ReparseSafeFileOpen.OpenRead(path))
            {
                using var reader = new StreamReader(file);
                var text = new System.Text.StringBuilder();
                var buffer = new char[2048];
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    if (text.Length + count > LyricsTranslationOutput.MaximumOutputBytes) return null;
                    text.Append(buffer, 0, count);
                }
                result = text.ToString();
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return result;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    public async Task WriteAsync(string key, string validatedJson, CancellationToken token)
    {
        ValidateKey(key);
        if (System.Text.Encoding.UTF8.GetByteCount(validatedJson) > LyricsTranslationOutput.MaximumOutputBytes)
            throw new InvalidDataException("Translation cache entry exceeds budget.");
        var final = ReparseSafePathPolicy.PrepareContainedFileDestination(root, key + ".json");
        var temporary = ReparseSafePathPolicy.PrepareContainedFileDestination(root, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, validatedJson, token).ConfigureAwait(false);
            ReparseSafePathPolicy.RevalidatePreparedDestination(root, final);
            File.Move(temporary, final, true);
            Trim();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task ClearAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(root)) return Task.CompletedTask;
        foreach (var file in Directory.EnumerateFiles(root, "*.json", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            var key = Path.GetFileNameWithoutExtension(file);
            if (key.Length != 64 || !key.All(Uri.IsHexDigit)) continue;
            File.Delete(ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(root, Path.GetFileName(file)));
        }
        return Task.CompletedTask;
    }

    private void Trim()
    {
        // Account for all owned entries. A truncated directory sample can remain
        // below budget forever while files outside that sample grow without bound.
        var files = Directory.EnumerateFiles(root, "*.json", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(path => Path.GetFileNameWithoutExtension(path) is { Length: 64 } name && name.All(Uri.IsHexDigit))
            .Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var size = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (size <= MaximumBytes) break;
            var length = file.Length;
            var path = ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(root, file.Name);
            File.Delete(path);
            size -= length;
        }
    }

    private static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid cache key.", nameof(key));
    }
}
