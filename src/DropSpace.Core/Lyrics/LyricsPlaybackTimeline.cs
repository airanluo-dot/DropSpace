using System.Runtime.CompilerServices;

namespace DropSpace.Core.Lyrics;

// All displays use the same sequence of foreground cues, including when an earlier
// singer resumes after an overlapping line. Index once per immutable list, then binary-search.
internal sealed class LyricsPlaybackTimeline
{
    private static readonly ConditionalWeakTable<IReadOnlyList<LyricsLine>, LyricsPlaybackTimeline> Cache = new();
    private readonly List<Cue> _cues = [];
    internal readonly record struct Cue(TimeSpan Start, int Active, int Anchor, int Next);

    internal static LyricsPlaybackTimeline For(IReadOnlyList<LyricsLine> lines) =>
        Cache.GetValue(lines, static list => new(list));

    private LyricsPlaybackTimeline(IReadOnlyList<LyricsLine> lines)
    {
        var edges = new List<(TimeSpan At, int Index, bool Begins, bool Vocal)>(lines.Count * 2);
        var hasBacking = lines.Any(line => line.IsBackground);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            edges.Add((line.Start, i, true, false));
            if (line.End > line.Start) edges.Add((line.End, i, false, false));
            if (!hasBacking) continue;
            // A line remains on display between words. When another voice is singing
            // during that gap, distinguish the vocal intervals from the enclosing line.
            if (line.Words.Count == 0)
            {
                edges.Add((line.Start, i, true, true));
                if (line.End > line.Start) edges.Add((line.End, i, false, true));
                continue;
            }
            foreach (var word in line.Words)
            {
                var start = word.Start > line.Start ? word.Start : line.Start;
                var end = word.End < line.End ? word.End : line.End;
                if (end <= start) continue;
                edges.Add((start, i, true, true));
                edges.Add((end, i, false, true));
            }
        }
        edges.Sort();
        var main = new SortedSet<int>();
        var backing = new SortedSet<int>();
        var singingMain = new SortedSet<int>();
        var singingBacking = new SortedSet<int>();
        var vocalCounts = hasBacking ? new int[lines.Count] : [];
        var active = -1;
        var anchor = -1;
        for (var i = 0; i < edges.Count;)
        {
            var at = edges[i].At;
            do
            {
                var edge = edges[i++];
                var background = lines[edge.Index].IsBackground;
                if (edge.Vocal)
                {
                    var singing = background ? singingBacking : singingMain;
                    vocalCounts[edge.Index] += edge.Begins ? 1 : -1;
                    if (vocalCounts[edge.Index] > 0) singing.Add(edge.Index); else singing.Remove(edge.Index);
                }
                else
                {
                    var voices = background ? backing : main;
                    if (edge.Begins) voices.Add(edge.Index); else voices.Remove(edge.Index);
                }
            } while (i < edges.Count && edges[i].At == at);
            // At identical starts the last eligible row wins. A backing vocal takes
            // the foreground when the lead is between words or has finished singing.
            var foreground = singingMain.Count > 0 ? singingMain.Max : singingBacking.Count > 0 ? singingBacking.Max
                : main.Count > 0 ? main.Max : backing.Count > 0 ? backing.Max : -1;
            if (foreground == active) continue;
            active = foreground;
            if (active >= 0) anchor = active;
            _cues.Add(new(at, active, anchor, -1));
        }

        var next = -1;
        for (var i = _cues.Count - 1; i >= 0; i--)
        {
            var cue = _cues[i];
            _cues[i] = cue with { Next = next };
            if (cue.Active >= 0) next = cue.Active;
        }
    }

    internal Cue At(TimeSpan time)
    {
        var low = 0;
        var high = _cues.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_cues[middle].Start <= time) low = middle + 1; else high = middle;
        }
        if (low > 0) return _cues[low - 1];
        // Pre-roll previews the first foreground row and its successor.
        return _cues.Count > 0 ? new(TimeSpan.Zero, -1, _cues[0].Active, _cues[0].Next) : new(TimeSpan.Zero, -1, -1, -1);
    }
}
