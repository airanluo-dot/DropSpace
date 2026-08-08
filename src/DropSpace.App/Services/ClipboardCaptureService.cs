using System.Threading.Channels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DropSpace.App.Services;

public enum ClipboardRecordingState
{
    Recording,
    Paused,
    Error,
}

public sealed record ClipboardCaptureStatus(
    ClipboardRecordingState State,
    long ObservedEvents,
    long CapturedItems,
    long DroppedEvents,
    string? Message);

public sealed class ClipboardCaptureService : IAsyncDisposable
{
    private readonly IItemRepository _repository;
    private readonly ISettingsService _settingsService;
    private readonly IPayloadStore _payloadStore;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<ClipboardCaptureService> _logger;
    private readonly Channel<CaptureSignal> _signals = Channel.CreateBounded<CaptureSignal>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private Task? _worker;
    private AppSettings _settings = new();
    private volatile bool _paused;
    private volatile bool _initialized;
    private int _pauseGeneration;
    private long _observedEvents;
    private long _capturedItems;
    private long _droppedEvents;
    private string? _selfFingerprint;
    private DateTimeOffset _selfWriteExpiresUtc;
    private DateTimeOffset _lastRetentionUtc = DateTimeOffset.MinValue;
    private int _disposeStarted;

    public ClipboardCaptureService(
        IItemRepository repository,
        ISettingsService settingsService,
        IPayloadStore payloadStore,
        DispatcherQueue dispatcher,
        ILogger<ClipboardCaptureService> logger)
    {
        _repository = repository;
        _settingsService = settingsService;
        _payloadStore = payloadStore;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public event EventHandler<ClipboardCaptureStatus>? StatusChanged;

    public event EventHandler<DropItem>? ItemCaptured;

    public ClipboardCaptureStatus Status => CreateStatus(null);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            _settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            _paused = _settings.ClipboardPaused;
            Clipboard.ContentChanged += OnClipboardContentChanged;
            _worker = Task.Run(ProcessSignalsAsync, CancellationToken.None);
            _initialized = true;
            PublishStatus(_paused ? "Recording remains paused from the previous session." : null);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            Interlocked.Increment(ref _pauseGeneration);
            await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _commitGate.Release();
            _settings = _settings with { ClipboardPaused = true };
            await _settingsService.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            PublishStatus("Clipboard recording paused.");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_paused)
            {
                return;
            }

            _settings = _settings with { ClipboardPaused = false };
            await _settingsService.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            _paused = false;
            Interlocked.Increment(ref _pauseGeneration);
            PublishStatus("Clipboard recording resumed.");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = settings;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = FingerprintService.ForText(text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
        MarkSelfWrite(fingerprint);
        await _dispatcher.EnqueueAsync(() =>
        {
            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.SetText(text);
            Clipboard.SetContent(package);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public async Task CopyImageAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var absolutePath = _payloadStore.ResolvePath(relativePath);
        var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        MarkSelfWrite(FingerprintService.ForBytes(bytes));

        await _dispatcher.EnqueueAsync(async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(absolutePath);
            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(package);
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        if (_initialized)
        {
            Clipboard.ContentChanged -= OnClipboardContentChanged;
        }

        _signals.Writer.TryComplete();
        _shutdown.Cancel();
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
        _stateGate.Dispose();
        _commitGate.Dispose();
    }

    private void OnClipboardContentChanged(object? sender, object eventArgs)
    {
        var observed = Interlocked.Increment(ref _observedEvents);
        if (_paused)
        {
            PublishStatus(null);
            return;
        }

        var signal = new CaptureSignal(observed, Volatile.Read(ref _pauseGeneration), DateTimeOffset.UtcNow);
        if (!_signals.Writer.TryWrite(signal))
        {
            Interlocked.Increment(ref _droppedEvents);
            PublishStatus("Clipboard activity exceeded the bounded capture queue; an event was skipped.");
        }
    }

    private async Task ProcessSignalsAsync()
    {
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (_paused || signal.PauseGeneration != Volatile.Read(ref _pauseGeneration))
                {
                    continue;
                }

                try
                {
                    var snapshot = await ReadSnapshotAsync(_shutdown.Token).ConfigureAwait(false);
                    if (snapshot is null || IsSelfWrite(snapshot.Fingerprint))
                    {
                        continue;
                    }

                    if (_paused || signal.PauseGeneration != Volatile.Read(ref _pauseGeneration))
                    {
                        continue;
                    }

                    DropItem item;
                    await _commitGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    try
                    {
                        if (_paused || signal.PauseGeneration != Volatile.Read(ref _pauseGeneration))
                        {
                            continue;
                        }

                        if (snapshot.Text is not null)
                        {
                            if (snapshot.Text.Length > _settings.MaxTextCharacters)
                            {
                                _logger.LogWarning("Clipboard text skipped because it exceeded the configured character limit.");
                                continue;
                            }

                            item = await _repository.AddTextAsync(
                                    ContentClassifier.CreateTextCandidate(snapshot.Text),
                                    _shutdown.Token)
                                .ConfigureAwait(false);
                        }
                        else if (snapshot.ImageBytes is not null)
                        {
                            if (!_settings.CaptureImages)
                            {
                                continue;
                            }

                            await using var stream = new MemoryStream(snapshot.ImageBytes, writable: false);
                            var payload = await _payloadStore.WriteAsync(
                                    "images",
                                    stream,
                                    _settings.MaxImageBytes,
                                    _shutdown.Token)
                                .ConfigureAwait(false);
                            try
                            {
                                item = await _repository.AddImageAsync(
                                        new ImageCandidate(
                                            snapshot.Fingerprint,
                                            snapshot.Width,
                                            snapshot.Height,
                                            snapshot.ImageBytes.LongLength,
                                            snapshot.MimeType ?? "image/png",
                                            snapshot.HasAlpha,
                                            payload),
                                        _shutdown.Token)
                                    .ConfigureAwait(false);
                                if (item.Payload?.Id != payload.Id)
                                {
                                    await _payloadStore.DeleteAsync(payload.RelativePath, _shutdown.Token).ConfigureAwait(false);
                                }
                            }
                            catch
                            {
                                await _payloadStore.DeleteAsync(payload.RelativePath, _shutdown.Token).ConfigureAwait(false);
                                throw;
                            }
                        }
                        else
                        {
                            continue;
                        }
                    }
                    finally
                    {
                        _commitGate.Release();
                    }

                    Interlocked.Increment(ref _capturedItems);
                    ItemCaptured?.Invoke(this, item);
                    PublishStatus(null);
                    await ApplyRetentionIfDueAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Clipboard event could not be captured.");
                    PublishStatus("A clipboard item could not be captured; recording remains active.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Clipboard capture worker stopped unexpectedly.");
            PublishStatus("Clipboard recording stopped after an internal error.", ClipboardRecordingState.Error);
        }
    }

    private Task<ClipboardSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken) =>
        _dispatcher.EnqueueAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = Clipboard.GetContent();

            if (view.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await view.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                if (stream.Size == 0 || stream.Size > (ulong)_settings.MaxImageBytes)
                {
                    return null;
                }

                var decoder = await BitmapDecoder.CreateAsync(stream);
                var pixels = checked((long)decoder.PixelWidth * decoder.PixelHeight);
                if (pixels > _settings.MaxImagePixels)
                {
                    return null;
                }

                using var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
                using var encodedStream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encodedStream);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                if (encodedStream.Size == 0 || encodedStream.Size > (ulong)_settings.MaxImageBytes)
                {
                    return null;
                }

                var bytes = new byte[checked((int)encodedStream.Size)];
                using var reader = new DataReader(encodedStream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)bytes.Length);
                reader.ReadBytes(bytes);
                return new ClipboardSnapshot(
                    null,
                    bytes,
                    FingerprintService.ForBytes(bytes),
                    checked((int)decoder.PixelWidth),
                    checked((int)decoder.PixelHeight),
                    decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore,
                    "image/png");
            }

            if (view.Contains(StandardDataFormats.Text))
            {
                var text = await view.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
                return new ClipboardSnapshot(
                    normalized,
                    null,
                    FingerprintService.ForText(normalized),
                    0,
                    0,
                    null,
                    null);
            }

            return null;
        });

    private async Task ApplyRetentionIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRetentionUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastRetentionUtc = now;
        var result = await _repository.ApplyRetentionAsync(
                now.AddDays(-_settings.RetentionDays),
                _settings.RetentionItemCount,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var path in result.PayloadPaths)
        {
            try
            {
                await _payloadStore.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Deferred payload cleanup failed.");
            }
        }
    }

    private void MarkSelfWrite(string fingerprint)
    {
        _selfFingerprint = fingerprint;
        _selfWriteExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(3);
    }

    private bool IsSelfWrite(string fingerprint) =>
        DateTimeOffset.UtcNow <= _selfWriteExpiresUtc &&
        string.Equals(fingerprint, _selfFingerprint, StringComparison.Ordinal);

    private ClipboardCaptureStatus CreateStatus(string? message, ClipboardRecordingState? state = null) =>
        new(
            state ?? (_paused ? ClipboardRecordingState.Paused : ClipboardRecordingState.Recording),
            Interlocked.Read(ref _observedEvents),
            Interlocked.Read(ref _capturedItems),
            Interlocked.Read(ref _droppedEvents),
            message);

    private void PublishStatus(string? message, ClipboardRecordingState? state = null) =>
        StatusChanged?.Invoke(this, CreateStatus(message, state));

    private sealed record CaptureSignal(long Sequence, int PauseGeneration, DateTimeOffset ObservedAtUtc);

    private sealed record ClipboardSnapshot(
        string? Text,
        byte[]? ImageBytes,
        string Fingerprint,
        int Width,
        int Height,
        bool? HasAlpha,
        string? MimeType);
}
