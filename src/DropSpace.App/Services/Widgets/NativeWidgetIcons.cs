using DropSpace.Core.Widgets;

namespace DropSpace.App.Services.Widgets;

/// <summary>Shared system-font glyphs for native widget identities.</summary>
internal static class NativeWidgetIcons
{
    // Segoe Fluent Icons names and code points:
    // https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font
    public static string Glyph(NativeWidgetId id) => id switch
    {
        NativeWidgetId.Clock => "\uE917", // Clock
        NativeWidgetId.Calendar => "\uE787", // Calendar
        NativeWidgetId.ResourceUsage => "\uE9D9", // Diagnostic
        NativeWidgetId.Settings => "\uE713", // Settings
        NativeWidgetId.Battery => "\uE850", // Battery0
        NativeWidgetId.Uptime => "\uE7E8", // PowerButton
        NativeWidgetId.Stopwatch => "\uE916", // Stopwatch
        NativeWidgetId.ClipboardPause => "\uE77F", // Paste
        NativeWidgetId.UtcClock => "\uE909", // World
        NativeWidgetId.FocusTimer => "\uF272", // Bullseye
        NativeWidgetId.Countdown => "\uE916", // Stopwatch
        NativeWidgetId.DiskSpace => "\uEDA2", // HardDrive
        NativeWidgetId.NetworkStatus => "\uE968", // Network
        NativeWidgetId.Calculator => "\uE8EF", // Calculator
        NativeWidgetId.PinnedItems => "\uE718", // Pin
        NativeWidgetId.WeekProgress => "\uE8C0", // CalendarWeek
        _ => "\uE713",
    };
}
