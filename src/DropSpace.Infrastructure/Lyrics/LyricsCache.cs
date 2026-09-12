using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Bounded process-memory cache. Local LRC bypasses it to observe user edits.</summary>
public sealed class LyricsCache
{
    private const int MaximumEntries = 32;
    private const int MaximumCharacters = 4 * 1024 * 1024;
    private readonly Dictionary<string, (LyricsDocument Document, DateTimeOffset Time, int Size)> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _characters;
    public bool TryGet(string key, out LyricsDocument document)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.Time < TimeSpan.FromHours(2))
            { document = entry.Document; return true; }
            document = LyricsDocument.Empty;
            return false;
        }
    }
    public void Put(string key, LyricsDocument document)
    {
        var size = document.Lines.Sum(line => line.Text.Length + (line.Secondary?.Length ?? 0) + line.Words.Sum(word => word.Text.Length));
        if (size > MaximumCharacters || document.Lines.Count == 0) return;
        lock (_gate)
        {
            if (_entries.Remove(key, out var previous)) _characters -= previous.Size;
            while (_entries.Count >= MaximumEntries || _characters + size > MaximumCharacters)
            {
                var oldest = _entries.MinBy(entry => entry.Value.Time);
                _entries.Remove(oldest.Key);
                _characters -= oldest.Value.Size;
            }
            _entries[key] = (document, DateTimeOffset.UtcNow, size);
            _characters += size;
        }
    }
    public void Clear() { lock (_gate) { _entries.Clear(); _characters = 0; } }
}
