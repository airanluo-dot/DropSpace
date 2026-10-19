namespace DropSpace.Core.Models;

public static class KeyboardShortcutPolicy
{
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        ["Space"] = "Ctrl+1", ["Clipboard"] = "Ctrl+2", ["Pinned"] = "Ctrl+3",
        ["Search"] = "Ctrl+F", ["SearchAlternate"] = "Ctrl+L",
        ["Settings"] = "Ctrl+Shift+S", ["SettingsAlternate"] = "Ctrl+,", ["Undo"] = "Ctrl+Z",
    };

    public static string Get(AppSettings settings, string id) =>
        settings.KeyboardShortcuts.TryGetValue(id, out var value) ? value : Defaults[id];

    public static void Validate(AppSettings settings, bool includeGlobal = false)
    {
        if (settings.KeyboardShortcuts is null || settings.KeyboardShortcuts.Count > Defaults.Count ||
            settings.KeyboardShortcuts.Keys.Any(id => !Defaults.ContainsKey(id)))
            throw new ArgumentException("Invalid keyboard shortcut settings.");
        var gestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includeGlobal) gestures.Add(SettingsValidationPolicy.CanonicalizeHotkey(settings.QuickPanelHotkey)!);
        foreach (var id in Defaults.Keys)
        {
            var gesture = SettingsValidationPolicy.CanonicalizeHotkey(Get(settings, id));
            if (gesture is null || !gestures.Add(gesture)) throw new ArgumentException("Invalid or duplicate keyboard shortcut.");
        }
    }

    public static uint KeyCode(string key) => key switch
    {
        "Space" => 0x20, "," => 0xBC,
        _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToUpperInvariant(key[0]),
        _ when key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var f) && f is >= 1 and <= 24 => (uint)(0x6F + f),
        _ => 0,
    };

    public static string? KeyName(uint code) => code switch
    {
        0x20 => "Space", 0xBC => ",", >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)code).ToString(),
        >= 0x70 and <= 0x87 => "F" + (code - 0x6F), _ => null,
    };
}
