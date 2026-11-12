using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

internal static class LyricWindowFrames
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowBounds bounds);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    internal static void UseScreenCoordinates() => SetProcessDpiAwarenessContext(-4);

    private sealed record Region(nint Window, AutomationElement Lyrics, AutomationElement? Footer);
    private sealed record Picture(DateTimeOffset Started, DateTimeOffset Finished, string File);

    internal static async Task RecordAsync(int applePid, int dropPid, string directory, CancellationToken stop)
    {
        Directory.CreateDirectory(directory);
        using var log = new StreamWriter(new FileStream(Path.Combine(directory, "pairs.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Region? apple = null, drop = null;
        while (!stop.IsCancellationRequested)
        {
            var sample = DateTimeOffset.UtcNow;
            try
            {
                apple ??= FindRegion(applePid, true);
                drop ??= FindRegion(dropPid, false);
                if (apple != null && drop != null)
                {
                    var prefix = sample.ToString("HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
                    var pictures = await Task.WhenAll(
                        Task.Run(() => Capture(apple, directory, prefix + "-apple.png")),
                        Task.Run(() => Capture(drop, directory, prefix + "-drop.png")));
                    await log.WriteLineAsync(JsonSerializer.Serialize(new { sample, apple = pictures[0], drop = pictures[1] }));
                    if (pictures[0] == null) apple = null;
                    if (pictures[1] == null) drop = null;
                }
                else await log.WriteLineAsync(JsonSerializer.Serialize(new { sample, applePane = apple != null, dropPane = drop != null }));
            }
            catch (ElementNotAvailableException) { apple = null; drop = null; }
            try { await Task.Delay(500, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        }
    }

    private static Region? FindRegion(int pid, bool apple)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
        foreach (AutomationElement window in windows)
        {
            var id = apple ? "LyricsMenuButton" : "MusicWindowLyricsContent";
            var anchor = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (anchor == null || anchor.Current.IsOffscreen) continue;
            var region = apple ? TreeWalker.RawViewWalker.GetParent(anchor) : anchor;
            if (region == null || (apple && region.Current.AutomationId != "PaneRoot")) continue;
            return new(window.Current.NativeWindowHandle, region, apple ? anchor : null);
        }
        return null;
    }

    private static Picture? Capture(Region target, string directory, string name)
    {
        if (IsIconic(target.Window) || target.Lyrics.Current.IsOffscreen ||
            !GetWindowRect(target.Window, out var window)) return null;
        var lyrics = target.Lyrics.Current.BoundingRectangle;
        if (lyrics.IsEmpty) return null;
        int width = window.Right - window.Left, height = window.Bottom - window.Top;
        var bottom = target.Footer?.Current.BoundingRectangle.Top ?? lyrics.Bottom;
        var clip = Rectangle.Intersect(new(0, 0, width, height), Rectangle.FromLTRB(
            (int)Math.Ceiling(lyrics.Left) - window.Left, (int)Math.Ceiling(lyrics.Top) - window.Top,
            (int)Math.Floor(lyrics.Right) - window.Left, (int)Math.Floor(bottom) - window.Top));
        if (clip.Width <= 0 || clip.Height <= 0) return null;
        using var bitmap = new Bitmap(width, height);
        var started = DateTimeOffset.UtcNow;
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(target.Window, dc, 2)) return null; }
            finally { graphics.ReleaseHdc(dc); }
        }
        var finished = DateTimeOffset.UtcNow;
        using var crop = bitmap.Clone(clip, PixelFormat.Format32bppArgb);
        crop.Save(Path.Combine(directory, name), ImageFormat.Png);
        return new(started, finished, name);
    }
}
