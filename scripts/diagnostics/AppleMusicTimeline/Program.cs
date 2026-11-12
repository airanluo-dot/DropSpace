using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using Windows.Media.Control;

var captureFrames = args.Contains("--frames");
args = args.Where(arg => arg != "--frames").ToArray();
if (args.Length is < 2 or > 3 || !int.TryParse(args[1], out var duration) || duration < 1)
{
    Console.Error.WriteLine("Usage: AppleMusicTimeline <new-output.jsonl> <seconds> [DropSpace-PID] [--frames]");
    return 2;
}

LyricWindowFrames.UseScreenCoordinates();
using var apple = Process.GetProcessesByName("AppleMusic").FirstOrDefault(process => process.MainWindowHandle != 0);
if (apple == null)
{
    Console.Error.WriteLine("Open the Apple Music window and play a track before recording.");
    return 2;
}
var appleWindow = AutomationElement.FromHandle(apple.MainWindowHandle);
var appleElapsed = Find(appleWindow, "CurrentTime");
var appleProgress = Slider(Find(appleWindow, "LCDScrubber"));
var copies = Process.GetProcessesByName("DropSpace");
using var drop = args.Length == 3
    ? copies.FirstOrDefault(process => process.Id.ToString() == args[2])
    : copies.Length == 1 ? copies[0] : null;
if (drop == null)
{
    Console.Error.WriteLine("Open DropSpace and supply its PID if more than one copy is running.");
    return 2;
}
AutomationElement? dropElapsed = null, dropLine = null, dropStatus = null;
RangeValuePattern? dropProgress = null;
void BindDropSpace()
{
    var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
        new PropertyCondition(AutomationElement.ProcessIdProperty, drop.Id));
    foreach (AutomationElement window in windows)
    {
        if (Find(window, "CurrentLyricsViewport") == null) continue;
        dropElapsed = Find(window, "ElapsedLabel");
        dropProgress = Slider(Find(window, "Progress"));
        dropLine = Find(window, "OriginalLyric");
        dropStatus = Find(window, "LyricsStatusText");
        break;
    }
    if (dropProgress == null)
        foreach (AutomationElement window in windows)
            if (Slider(Find(window, "MusicWindowSeek")) is { } seek) { dropProgress = seek; break; }
}
BindDropSpace();

var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
const string appleId = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
var session = manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Equals(appleId, StringComparison.OrdinalIgnoreCase));
if (session == null)
{
    Console.Error.WriteLine("Play a track in Apple Music so that Windows can expose its media timeline.");
    return 2;
}
using var output = new StreamWriter(new FileStream(args[0], FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
using var captures = new CancellationTokenSource();
var frames = captureFrames
    ? Task.Run(() => LyricWindowFrames.RecordAsync(apple.Id, drop.Id, Path.GetFullPath(args[0]) + ".frames", captures.Token))
    : Task.CompletedTask;
var stopwatch = Stopwatch.StartNew();
TimeSpan previousDuration = TimeSpan.MinValue, lastMetadataRead = TimeSpan.FromSeconds(-2);
string title = "", artist = "", album = "";
var counts = new Dictionary<(string Title, string Artist, string Album), SongSamples>();
while (stopwatch.Elapsed.TotalSeconds < duration)
{
    var started = DateTimeOffset.UtcNow;
    var raw = session.GetTimelineProperties();
    var playback = session.GetPlaybackInfo();
    if (raw.EndTime != previousDuration || (stopwatch.Elapsed - lastMetadataRead).TotalSeconds >= 1)
    {
        appleElapsed ??= Find(appleWindow, "CurrentTime");
        appleProgress ??= Slider(Find(appleWindow, "LCDScrubber"));
        if (dropProgress == null) BindDropSpace();
        var track = await session.TryGetMediaPropertiesAsync();
        title = track.Title; artist = track.Artist; album = track.AlbumTitle;
        previousDuration = raw.EndTime;
        lastMetadataRead = stopwatch.Elapsed;
    }
    var appleText = Read(() => appleElapsed?.Current.Name);
    var appleSeconds = ReadSeconds(appleProgress);
    var dropText = Read(() => dropElapsed?.Current.Name);
    var dropSeconds = ReadSeconds(dropProgress);
    var lyric = Read(() => dropLine?.Current.Name);
    var status = Read(() => dropStatus?.Current.Name);
    var finished = DateTimeOffset.UtcNow;
    var key = (title, artist, album);
    if (!counts.TryGetValue(key, out var sample)) counts[key] = sample = new();
    sample.Add(appleSeconds, dropSeconds, raw.Position.TotalSeconds, finished - started);
    await output.WriteLineAsync(JsonSerializer.Serialize(new
    {
        started, finished, title, artist, album,
        state = playback.PlaybackStatus.ToString(), rate = playback.PlaybackRate ?? 1,
        native = raw.Position.TotalSeconds, start = raw.StartTime.TotalSeconds, end = raw.EndTime.TotalSeconds,
        nativeTimestamp = raw.LastUpdatedTime, appleText, appleSeconds, dropText, dropSeconds, lyric, status,
        applePid = apple.Id, dropPid = drop.Id
    }));
    await Task.Delay(75);
}
captures.Cancel();
await frames;
var summary = counts.Select(kv => new { kv.Key.Title, kv.Key.Artist, kv.Key.Album, Statistics = kv.Value });
await File.WriteAllTextAsync(args[0] + ".summary.json", JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static AutomationElement? Find(AutomationElement parent, string id) =>
    parent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
static RangeValuePattern? Slider(AutomationElement? element) =>
    element?.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern) == true ? (RangeValuePattern)pattern : null;
static double? ReadSeconds(RangeValuePattern? slider)
{
    try { return slider?.Current.Value; }
    catch (ElementNotAvailableException) { return null; }
}
static string? Read(Func<string?> getter)
{
    try { return getter(); }
    catch (ElementNotAvailableException) { return null; }
}

internal sealed class SongSamples
{
    public int Samples { get; private set; }
    public int Compared { get; private set; }
    public int SameDisplayedSecond { get; private set; }
    public int DropAhead { get; private set; }
    public int DropBehind { get; private set; }
    public int DifferentSecondsWithNativeAtApple { get; private set; }
    public int DifferentSecondsWithNativeAtDrop { get; private set; }

    public void Add(double? apple, double? drop, double native, TimeSpan readTime)
    {
        Samples++;
        if (apple == null || drop == null || readTime.TotalMilliseconds > 250) return;
        Compared++;
        var dropSecond = Math.Floor(drop.Value);
        var appleSecond = Math.Floor(apple.Value);
        var difference = dropSecond - appleSecond;
        if (difference > 0) DropAhead++;
        else if (difference < 0) DropBehind++;
        else { SameDisplayedSecond++; return; }
        if (Math.Floor(native) == appleSecond) DifferentSecondsWithNativeAtApple++;
        if (Math.Floor(native) == dropSecond) DifferentSecondsWithNativeAtDrop++;
    }
}
