using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

internal sealed class PlainLyricsMetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    internal readonly record struct Sample(string Instrument, double Value, Dictionary<string, string> Tags);
    internal ConcurrentQueue<Sample> Samples { get; } = new();

    internal PlainLyricsMetricCapture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PlainLyricsMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
        _listener.Start();
    }

    private void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        Samples.Enqueue(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value?.ToString() ?? string.Empty)));

    internal Sample[] Stage(string stage) => Samples.Where(x => x.Tags.GetValueOrDefault("stage") == stage).ToArray();
    internal int Events(string value) => (int)Samples.Where(x => x.Tags.GetValueOrDefault("event") == value).Sum(x => x.Value);
    internal void SaveEvidence()
    {
        // Optional cloud experiment output. Values contain only the production metric's fixed labels.
        var directory = Environment.GetEnvironmentVariable("DROPSPACE_HOST_METRICS_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(Samples.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose() => _listener.Dispose();
}
