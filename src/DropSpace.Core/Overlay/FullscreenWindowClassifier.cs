namespace DropSpace.Core.Overlay;

public readonly record struct ForegroundWindowFacts(
    bool IsVisible,
    bool IsCloaked,
    bool IsIconic,
    bool IsDesktopOrShellWindow,
    bool IsOnTargetMonitor,
    bool CoversTargetMonitor,
    long Style,
    long ExtendedStyle,
    string ClassName);

/// <summary>
/// Separates actual user full-screen windows from Windows Shell surfaces. The desktop happens to
/// cover a monitor, but it is not a full-screen application and must never suppress DropSpace.
/// </summary>
public static class FullscreenWindowClassifier
{
    private const long WindowStyleChild = 0x40000000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;

    private static readonly HashSet<string> ShellClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    public static bool IsFullscreenApplication(ForegroundWindowFacts facts) =>
        facts.IsVisible &&
        !facts.IsCloaked &&
        !facts.IsIconic &&
        !facts.IsDesktopOrShellWindow &&
        !ShellClassNames.Contains(facts.ClassName) &&
        facts.IsOnTargetMonitor &&
        facts.CoversTargetMonitor &&
        (facts.Style & WindowStyleChild) == 0 &&
        (facts.ExtendedStyle & ExtendedStyleToolWindow) == 0;
}
