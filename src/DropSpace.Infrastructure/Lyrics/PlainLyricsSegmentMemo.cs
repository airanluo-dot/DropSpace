using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>One song request owns this bounded memo. It retains accepted text, never tasks or host IDs.</summary>
internal sealed class PlainLyricsSegmentMemo
{
    internal const int MaximumEntries = 128;
    internal const int MaximumBytes = 256 * 1024;
    private readonly Dictionary<Key, string> _accepted = [];
    private int _bytes;

    // Compare exact UTF-8 bytes, including case/spacing, rather than a lossy digest or fuzzy source match.
    internal readonly record struct Key(string PromptUtf8, string Target, string InferenceIdentity, string AdmissionVersion);
    internal static Key CreateKey(string prompt, string target, string identity) => new(
        Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt)), LyricsTranslationPolicy.NormalizeLanguage(target),
        identity, LyricsLanguagePolicy.Version);

    internal void Clear() { _accepted.Clear(); _bytes = 0; }

    internal bool TryGet(Key key, out string text) => _accepted.TryGetValue(key, out text!);

    internal void Remember(Key key, string source, string text)
    {
        if (_accepted.ContainsKey(key) || _accepted.Count >= MaximumEntries || !PlainHyLyricsProtocol.IsCompleteLine(text)) return;
        // A whole display row may contain several physical segments. Each segment must independently
        // pass the same output admission, including protocol-leak and copied-source rejection.
        var projected = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.Zero, source, null, [])], LyricsProviderKind.LocalLrc);
        var json = JsonSerializer.Serialize(new[] { new { id = 0, text } });
        if (!LyricsTranslationOutput.TryApply(json, projected, [0], key.Target, out var accepted) ||
            !LyricsTranslationOutput.HasUsefulLocalTranslation(accepted, key.Target)) return;
        var bytes = key.PromptUtf8.Length * sizeof(char) + text.Length * sizeof(char) +
            (key.Target.Length + key.InferenceIdentity.Length + key.AdmissionVersion.Length) * sizeof(char);
        if (bytes > MaximumBytes - _bytes) return;
        _accepted.Add(key, text);
        _bytes += bytes;
    }
}
