using DropSpace.Core.Lyrics;

namespace DropSpace.App.Views.Music;

/// <summary>Keeps visual rows at stable original-line indices across progressive translations.</summary>
internal sealed class LyricsRowCollection<TRow>(int maximumRows, Func<LyricsLine, TRow> create,
    Action<TRow, LyricsLine> update) where TRow : class
{
    private readonly List<Entry> _rows = [];
    private IReadOnlyList<LyricsLine>? _lines;
    private string _trackIdentity = string.Empty;
    private string _options = string.Empty;
    public int Count => _rows.Count;
    public TRow this[int index] => _rows[index].Row;

    // A changed translation/origin/word frame does not change original-line identity.
    internal static bool SameOriginal(LyricsLine left, LyricsLine right) =>
        left.Start == right.Start && left.End == right.End && left.Text == right.Text;

    public bool Update(IReadOnlyList<LyricsLine> lines, string trackIdentity, string options)
    {
        var trackChanged = !string.Equals(_trackIdentity, trackIdentity, StringComparison.Ordinal);
        var optionsChanged = !string.Equals(_options, options, StringComparison.Ordinal);
        if (!trackChanged && !optionsChanged && ReferenceEquals(_lines, lines)) return false;
        if (trackChanged) _rows.Clear();
        var count = Math.Min(maximumRows, lines.Count);
        var structureChanged = trackChanged || count != _rows.Count;
        if (_rows.Count > count) _rows.RemoveRange(count, _rows.Count - count);
        for (var index = 0; index < count; index++)
        {
            var line = lines[index];
            if (index >= _rows.Count)
            {
                _rows.Add(new(line, create(line)));
                structureChanged = true;
            }
            else
            {
                var entry = _rows[index];
                if (!SameOriginal(entry.Line, line))
                {
                    _rows[index] = new(line, create(line));
                    structureChanged = true;
                }
                else if (optionsChanged || !ReferenceEquals(entry.Line, line))
                {
                    update(entry.Row, line);
                    entry.Line = line;
                }
            }
        }
        _lines = lines;
        _trackIdentity = trackIdentity;
        _options = options;
        return structureChanged;
    }

    private sealed class Entry(LyricsLine line, TRow row)
    {
        public LyricsLine Line { get; set; } = line;
        public TRow Row { get; } = row;
    }
}
