using System.Diagnostics;

namespace DropSpace.Rendering;

/// <summary>Explicit diagnostic counters only; no scheduling or GPU pipeline changes.</summary>
internal sealed class GlassRefreshMeasurement
{
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _lastTick;
    private TimeSpan? _lastCapture;
    private readonly record struct PresentSample(long SourceTimeTicks, long PresentStartQpc, long PresentEndQpc);
    private readonly List<PresentSample> _presentSamples = [];
    private int _ticks, _captures, _emptyPolls, _renders, _arrivals, _discarded, _skippedRenders;
    private readonly List<double> _tickIntervals = [], _captureIntervals = [], _captureAge = [], _renderCpu = [], _presentCpu = [];
    private readonly List<double> _arrivalAge = [], _pendingAge = [];
    private long _copiedPixels;
    internal void CopiedRegion(int width, int height) { lock (_gate) _copiedPixels += (long)width * height; }
    internal void Tick()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastTick != 0) Add(_tickIntervals, Stopwatch.GetElapsedTime(_lastTick, now).TotalMilliseconds);
            _lastTick = now; _ticks++;
        }
    }
    internal void Arrived(TimeSpan time, long receivedAt, bool replaced)
    {
        lock (_gate)
        {
            _arrivals++;
            if (replaced) _discarded++;
            Add(_arrivalAge, receivedAt * 1000d / Stopwatch.Frequency - time.TotalMilliseconds);
        }
    }
    internal void Capture(TimeSpan time, long receivedAt)
    {
        lock (_gate)
        {
            _captures++;
            var milliseconds = time.TotalMilliseconds;
            if (_lastCapture is { } previous) Add(_captureIntervals, (time - previous).TotalMilliseconds);
            _lastCapture = time;
            var now = Stopwatch.GetTimestamp();
            // WGC SystemRelativeTime and Stopwatch both use QueryPerformanceCounter on Windows.
            Add(_captureAge, now * 1000d / Stopwatch.Frequency - milliseconds);
            Add(_pendingAge, Stopwatch.GetElapsedTime(receivedAt, now).TotalMilliseconds);
        }
    }
    internal void EmptyPoll() { lock (_gate) _emptyPolls++; }
    internal void SkippedRender() { lock (_gate) _skippedRenders++; }
    internal void Render(long started) { lock (_gate) { _renders++; Add(_renderCpu, Stopwatch.GetElapsedTime(started).TotalMilliseconds); } }
    internal void Present(long started)
    {
        var ended = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            Add(_presentCpu, Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds);
            if (_lastCapture is { } source && _presentSamples.Count < 4096)
                _presentSamples.Add(new(source.Ticks, started, ended));
        }
    }
    private static void Add(List<double> values, double value) { if (values.Count < 4096) values.Add(value); }
    private static object Statistics(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { samples = sorted.Length, mean = sorted.Length == 0 ? 0 : sorted.Average(),
            p95 = sorted.Length == 0 ? 0 : sorted[(int)Math.Floor((sorted.Length - 1) * .95)],
            maximum = sorted.Length == 0 ? 0 : sorted[^1] };
    }
    internal object Complete(int captureWidth, int captureHeight, GlassFrame frame, double targetHz, nint swapChain)
    {
        lock (_gate)
        {
            var seconds = Stopwatch.GetElapsedTime(_started).TotalSeconds;
            return new { seconds, captureStrategy = "FreeThreadedLatestFrameRegionCopy", targetHz, frameDriven = true,
                swapChainAddress = swapChain == 0 ? null : $"0x{(nuint)swapChain:X16}",
                qpcFrequency = Stopwatch.Frequency, presentSamples = _presentSamples.ToArray(),
                configuredPollingHz = 0, monitorCheckHz = 4, copiedBackgroundPixels = _copiedPixels,
                receivedFrames = _arrivals, discardedPendingFrames = _discarded, receivedFps = _arrivals / seconds,
                ticks = _ticks, capturedFrames = _captures, skippedUnchangedRenders = _skippedRenders,
                emptyPolls = _emptyPolls, renderedFrames = _renders, captureFps = _captures / seconds, renderFps = _renders / seconds,
                captureWidth, captureHeight, islandWidth = frame.Width, islandHeight = frame.Height,
                tickIntervalMs = Statistics(_tickIntervals), consumedCaptureIntervalMs = Statistics(_captureIntervals),
                captureAgeAtReadMs = Statistics(_captureAge), captureAgeAtArrivalMs = Statistics(_arrivalAge),
                latestFrameWaitMs = Statistics(_pendingAge), renderCpuMs = Statistics(_renderCpu), presentCpuMs = Statistics(_presentCpu),
                boundary = "CPU wall times and consumed WGC frame timestamps only; GPU shader duration is not measured." };
        }
    }
}
