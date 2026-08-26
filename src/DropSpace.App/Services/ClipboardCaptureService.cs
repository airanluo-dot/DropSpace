using System.Threading.Channels;
using System.Runtime.InteropServices;
using System.Text.Json;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Core.Transfer;
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
    bool ListenerRegistered,
    DateTimeOffset? LastNotificationUtc,
    long ObservedEvents,
    long CapturedItems,
    long SuppressedConsecutiveDuplicates,
    long FailedReads,
    long DroppedEvents,
    string? Message);

public sealed class ClipboardCaptureService : IAsyncDisposable
{
    private readonly IItemRepository _repository;
    private readonly ISettingsService _settingsService;
    private readonly IPayloadStore _payloadStore;
    private readonly IFileReferenceService _fileReferences;
    private readonly ClipboardNotificationService _notifications;
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppStringLocalizer _strings;
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
    private readonly ConsecutiveClipboardCaptureCoordinator _consecutiveCaptures = new();
    private Task? _worker;
    private AppSettings _settings = new();
    private volatile bool _paused;
    private volatile bool _initialized;
    private int _pauseGeneration;
    private long _observedEvents;
    private long _capturedItems;
    private long _suppressedConsecutiveDuplicates;
    private long _failedReads;
    private long _droppedEvents;
    private string? _selfFingerprint;
    private DateTimeOffset _selfWriteExpiresUtc;
    private DateTimeOffset _lastRetentionUtc = DateTimeOffset.MinValue;
    private uint _lastProcessedClipboardSequence;
    private int _disposeStarted;

    public ClipboardCaptureService(
        IItemRepository repository,
        ISettingsService settingsService,
        IPayloadStore payloadStore,
        IFileReferenceService fileReferences,
        ClipboardNotificationService notifications,
        DispatcherQueue dispatcher,
        IAppStringLocalizer strings,
        ILogger<ClipboardCaptureService> logger)
    {
        _repository = repository;
        _settingsService = settingsService;
        _payloadStore = payloadStore;
        _fileReferences = fileReferences;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _strings = strings;
        _logger = logger;
    }

    public event EventHandler<ClipboardCaptureStatus>? StatusChanged;

    public event EventHandler<DropItem>? ItemCaptured;

    public ClipboardCaptureStatus Status => CreateStatus(null);

    public bool IsPaused => _paused;

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
            _notifications.ClipboardChanged += OnClipboardChanged;
            _notifications.StatusChanged += OnNotificationStatusChanged;
            _worker = Task.Run(ProcessSignalsAsync, CancellationToken.None);
            _initialized = true;
            PublishStatus(
                _notifications.Status.IsRegistered
                    ? _paused ? _strings.Get("ClipboardPausedAtStartup") : null
                    : _strings.Get("ClipboardListenerRegistrationFailed"),
                _notifications.Status.IsRegistered ? null : ClipboardRecordingState.Error);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        // Serialize the transition with repository commits. Taking commitGate first
        // means a pause request either precedes a remote import or waits for that
        // already-started import; no later import can pass the check while paused.
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
                _settings = _settings with { ClipboardPaused = true };
                await _settingsService.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
                PublishStatus(_strings.Get("ClipboardPaused"));
            }
            finally
            {
                _stateGate.Release();
            }
        }
        finally
        {
            _commitGate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_paused)
                {
                    return;
                }

                await _consecutiveCaptures.ResetAsync(cancellationToken).ConfigureAwait(false);

                _settings = _settings with { ClipboardPaused = false };
                await _settingsService.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
                _paused = false;
                Interlocked.Increment(ref _pauseGeneration);
                PublishStatus(_strings.Get("ClipboardResumed"));
            }
            finally
            {
                _stateGate.Release();
            }
        }
        finally
        {
            _commitGate.Release();
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

    public async Task<ClearResult> ClearHistoryAsync(
        DateTimeOffset? fromUtc,
        bool includePinned,
        CancellationToken cancellationToken = default)
    {
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _repository.ClearClipboardAsync(fromUtc, includePinned, cancellationToken)
                .ConfigureAwait(false);
            await _consecutiveCaptures.ResetAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _commitGate.Release();
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

    public async Task<DropItem> ImportRemoteAsync(ClipboardEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (_paused) throw new ClipboardPausedException();
        ClipboardEnvelopePolicy.Validate(envelope);
        DropItem item;
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Pause wins over a remote request even when the request raced with the
            // notification event. The check is inside the same gate as the repository
            // write, so a paused import can never create a history row.
            if (_paused) throw new ClipboardPausedException();
            if (envelope.IsTextLike)
            {
                item = await _repository.AddTextAsync(ContentClassifier.CreateTextCandidate(envelope.Text!), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var image = envelope.ImageBytes!;
                var dimensions = await ReadImageDimensionsAsync(image, cancellationToken).ConfigureAwait(false);
                await using var stream = new MemoryStream(image, writable: false);
                var payload = await _payloadStore.WriteAsync("images", stream, _settings.MaxImageBytes, cancellationToken).ConfigureAwait(false);
                try
                {
                    item = await _repository.AddImageAsync(
                        new ImageCandidate(envelope.Sha256, dimensions.Width, dimensions.Height, image.LongLength, envelope.Mime, dimensions.HasAlpha, payload),
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await _payloadStore.DeleteAsync(payload.RelativePath, cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }

            // Keep the commit gate through the final pause check and the optional
            // Windows clipboard mutation. PauseAsync cannot become effective between
            // this check and CopyTextAsync/CopyImageAsync.
            if (_paused) return item;

            ItemCaptured?.Invoke(this, item);
            if (item.Text?.InlineText is { } text)
            {
                await CopyTextAsync(text, cancellationToken).ConfigureAwait(false);
            }
            else if (item.Payload is { RelativePath: var relativePath })
            {
                await CopyImageAsync(relativePath, cancellationToken).ConfigureAwait(false);
            }

            return item;
        }
        finally
        {
            _commitGate.Release();
        }
    }

    private Task<(int Width, int Height, bool HasAlpha)> ReadImageDimensionsAsync(byte[] bytes, CancellationToken cancellationToken) =>
        _dispatcher.EnqueueAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return (checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight), decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore);
        });

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

    public async Task CopyFilesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var distinctPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctPaths.Length == 0)
        {
            throw new ArgumentException("At least one file-system path is required.", nameof(paths));
        }

        MarkSelfWrite(CreateFileClipboardFingerprint(distinctPaths));
        await _dispatcher.EnqueueAsync(async () =>
        {
            var storageItems = new List<IStorageItem>(distinctPaths.Length);
            foreach (var path in distinctPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                storageItems.Add(Directory.Exists(path)
                    ? await StorageFolder.GetFolderFromPathAsync(path)
                    : await StorageFile.GetFileFromPathAsync(path));
            }

            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.SetStorageItems(storageItems, readOnly: true);
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
            _notifications.ClipboardChanged -= OnClipboardChanged;
            _notifications.StatusChanged -= OnNotificationStatusChanged;
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
                _logger.LogInformation("Clipboard worker shutdown exceeded the bounded wait.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Clipboard worker shutdown was cancelled.");
            }
        }

        _shutdown.Dispose();
        _stateGate.Dispose();
        _commitGate.Dispose();
        _consecutiveCaptures.Dispose();
    }

    private void OnClipboardChanged(object? sender, ClipboardNotification notification)
    {
        var observed = Interlocked.Increment(ref _observedEvents);
        if (_paused)
        {
            PublishStatus(null);
            return;
        }

        var signal = new CaptureSignal(
            notification.SequenceNumber,
            observed,
            Volatile.Read(ref _pauseGeneration),
            notification.ObservedAtUtc);
        if (!_signals.Writer.TryWrite(signal))
        {
            Interlocked.Increment(ref _droppedEvents);
            PublishStatus(_strings.Get("ClipboardEventDropped"));
        }
    }

    private void OnNotificationStatusChanged(object? sender, ClipboardNotificationStatus status)
    {
        if (!status.IsRegistered)
        {
            PublishStatus(_strings.Get("ClipboardListenerUnavailable"), ClipboardRecordingState.Error);
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

                if (signal.ClipboardSequenceNumber != 0 &&
                    signal.ClipboardSequenceNumber == _lastProcessedClipboardSequence)
                {
                    _logger.LogInformation(
                        "Duplicate WM_CLIPBOARDUPDATE skipped for sequence {SequenceNumber}.",
                        signal.ClipboardSequenceNumber);
                    continue;
                }

                try
                {
                    var snapshot = await ReadSnapshotWithRetryAsync(signal, _shutdown.Token).ConfigureAwait(false);
                    _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                    if (snapshot is null || IsSelfWrite(snapshot.Fingerprint))
                    {
                        if (snapshot is not null)
                        {
                            _logger.LogInformation(
                                "Clipboard self-write suppressed for sequence {SequenceNumber}.",
                                signal.ClipboardSequenceNumber);
                        }

                        continue;
                    }

                    if (_paused || signal.PauseGeneration != Volatile.Read(ref _pauseGeneration))
                    {
                        continue;
                    }

                    IReadOnlyList<DropItem> items;
                    await _commitGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    try
                    {
                        if (_paused || signal.PauseGeneration != Volatile.Read(ref _pauseGeneration))
                        {
                            continue;
                        }

                        var capture = await _consecutiveCaptures.ExecuteAsync(
                                snapshot.Fingerprint,
                                token => CommitSnapshotAsync(snapshot, signal.ClipboardSequenceNumber, token),
                                committedItems => committedItems.Count > 0,
                                _shutdown.Token)
                            .ConfigureAwait(false);
                        if (capture.Suppressed)
                        {
                            Interlocked.Increment(ref _suppressedConsecutiveDuplicates);
                            _logger.LogInformation(
                                "Consecutive clipboard snapshot suppressed for sequence {SequenceNumber}.",
                                signal.ClipboardSequenceNumber);
                            PublishStatus(null);
                            continue;
                        }

                        items = capture.Value;
                        if (items.Count == 0)
                        {
                            continue;
                        }
                    }
                    finally
                    {
                        _commitGate.Release();
                    }

                    Interlocked.Add(ref _capturedItems, items.Count);
                    foreach (var item in items)
                    {
                        ItemCaptured?.Invoke(this, item);
                    }
                    PublishStatus(null);
                    await ApplyRetentionIfDueAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                    _logger.LogWarning(exception, "Clipboard event could not be captured.");
                    PublishStatus(_strings.Get("ClipboardItemCaptureFailed"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Clipboard capture worker stopped unexpectedly.");
            PublishStatus(_strings.Get("ClipboardCaptureStopped"), ClipboardRecordingState.Error);
        }
    }

    private async Task<ClipboardSnapshot?> ReadSnapshotWithRetryAsync(
        CaptureSignal signal,
        CancellationToken cancellationToken)
    {
        var delays = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(35),
            TimeSpan.FromMilliseconds(90),
            TimeSpan.FromMilliseconds(180),
            TimeSpan.FromMilliseconds(360),
            TimeSpan.FromMilliseconds(720),
            TimeSpan.FromMilliseconds(1200),
        };

        Exception? lastException = null;
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delays[attempt] > TimeSpan.Zero)
            {
                await Task.Delay(delays[attempt], cancellationToken).ConfigureAwait(false);
                var currentSequence = GetClipboardSequenceNumber();
                if (signal.ClipboardSequenceNumber != 0 &&
                    currentSequence != 0 &&
                    currentSequence != signal.ClipboardSequenceNumber)
                {
                    _logger.LogInformation(
                        "Clipboard retry abandoned because sequence advanced from {OriginalSequence} to {CurrentSequence}.",
                        signal.ClipboardSequenceNumber,
                        currentSequence);
                    return null;
                }
            }

            try
            {
                _logger.LogInformation(
                    "Clipboard snapshot read started for sequence {SequenceNumber}, attempt {Attempt}.",
                    signal.ClipboardSequenceNumber,
                    attempt + 1);
                var snapshot = await ReadSnapshotAsync(signal, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Clipboard snapshot read completed for sequence {SequenceNumber}; format {Format}.",
                    signal.ClipboardSequenceNumber,
                    snapshot?.FilePaths is not null ? "files" : snapshot?.Text is not null ? "text" : snapshot?.ImageBytes is not null ? "image" : "unsupported");
                return snapshot;
            }
            catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
            {
                lastException = exception;
                Interlocked.Increment(ref _failedReads);
                PublishStatus(_strings.Get("ClipboardBusyRetrying"));
                _logger.LogWarning(
                    exception,
                    "Transient clipboard read failure for sequence {SequenceNumber}, attempt {Attempt}.",
                    signal.ClipboardSequenceNumber,
                    attempt + 1);
            }
        }

        throw new InvalidOperationException(
            $"Clipboard sequence {signal.ClipboardSequenceNumber} remained unavailable after bounded retry.",
            lastException);
    }

    private Task<ClipboardSnapshot?> ReadSnapshotAsync(
        CaptureSignal signal,
        CancellationToken cancellationToken) =>
        _dispatcher.EnqueueAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = Clipboard.GetContent();

            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await view.GetStorageItemsAsync();
                var paths = storageItems
                    .Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (paths.Length > 0)
                {
                    return new ClipboardSnapshot(
                        null,
                        null,
                        paths,
                        CreateFileClipboardFingerprint(paths),
                        0,
                        0,
                        null,
                        null);
                }

                // A producer can publish the StorageItems format before its async
                // item payload is materialized. Treat that empty read as transient;
                // otherwise this WM_CLIPBOARDUPDATE would be marked processed and
                // the file/folder batch could never be captured.
                throw new COMException(
                    "Clipboard storage items are not ready.",
                    unchecked((int)0x8000000A)); // E_PENDING
            }

            if (view.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await view.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                if (stream.Size == 0 || stream.Size > (ulong)_settings.MaxImageBytes)
                {
                    return CreateRejectedSnapshot(signal, "image-byte-limit");
                }

                var decoder = await BitmapDecoder.CreateAsync(stream);
                var pixels = checked((long)decoder.PixelWidth * decoder.PixelHeight);
                if (pixels > _settings.MaxImagePixels)
                {
                    return CreateRejectedSnapshot(signal, "image-pixel-limit");
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
                    return CreateRejectedSnapshot(signal, "encoded-image-byte-limit");
                }

                var bytes = new byte[checked((int)encodedStream.Size)];
                using var reader = new DataReader(encodedStream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)bytes.Length);
                reader.ReadBytes(bytes);
                return new ClipboardSnapshot(
                    null,
                    bytes,
                    null,
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
                    return CreateRejectedSnapshot(signal, "empty-text");
                }

                var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
                return new ClipboardSnapshot(
                    normalized,
                    null,
                    null,
                    FingerprintService.ForText(normalized),
                    0,
                    0,
                    null,
                    null);
            }

            return CreateRejectedSnapshot(signal, "unsupported-format");
        });

    private static ClipboardSnapshot CreateRejectedSnapshot(CaptureSignal signal, string reason) =>
        new(
            null,
            null,
            null,
            FingerprintService.ForText(
                $"clipboard-observed-not-persisted\0{reason}\0{signal.ClipboardSequenceNumber}\0{signal.ObservedEventNumber}"),
            0,
            0,
            null,
            null);

    private async Task<IReadOnlyList<DropItem>> CommitSnapshotAsync(
        ClipboardSnapshot snapshot,
        uint sequenceNumber,
        CancellationToken cancellationToken)
    {
        if (snapshot.Text is not null)
        {
            if (snapshot.Text.Length > _settings.MaxTextCharacters)
            {
                _logger.LogWarning("Clipboard text skipped because it exceeded the configured character limit.");
                return [];
            }

            var item = await _repository.AddTextAsync(
                    ContentClassifier.CreateTextCandidate(snapshot.Text),
                    cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("Clipboard text committed for sequence {SequenceNumber}.", sequenceNumber);
            return [item];
        }

        if (snapshot.ImageBytes is not null)
        {
            if (!_settings.CaptureImages)
            {
                return [];
            }

            await using var stream = new MemoryStream(snapshot.ImageBytes, writable: false);
            var payload = await _payloadStore.WriteAsync(
                    "images",
                    stream,
                    _settings.MaxImageBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var item = await _repository.AddImageAsync(
                        new ImageCandidate(
                            snapshot.Fingerprint,
                            snapshot.Width,
                            snapshot.Height,
                            snapshot.ImageBytes.LongLength,
                            snapshot.MimeType ?? "image/png",
                            snapshot.HasAlpha,
                            payload),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (item.Payload?.Id != payload.Id)
                {
                    await _payloadStore.DeleteAsync(payload.RelativePath, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation("Clipboard image committed for sequence {SequenceNumber}.", sequenceNumber);
                return [item];
            }
            catch
            {
                await _payloadStore.DeleteAsync(payload.RelativePath, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        if (snapshot.FilePaths is null || !_settings.CaptureFiles)
        {
            return [];
        }

        if (snapshot.FilePaths.Count > _settings.MaxClipboardFileItems)
        {
            _logger.LogWarning(
                "Clipboard file batch skipped because item count {ItemCount} exceeded configured limit {Limit}.",
                snapshot.FilePaths.Count,
                _settings.MaxClipboardFileItems);
            return [];
        }

        var candidates = new List<FileCandidate>(snapshot.FilePaths.Count);
        long knownTotalBytes = 0;
        foreach (var path in snapshot.FilePaths)
        {
            try
            {
                var candidate = await _fileReferences.InspectAsync(path, cancellationToken).ConfigureAwait(false);
                if (candidate.EntryKind == FileEntryKind.Folder && !_settings.CaptureFolders)
                {
                    continue;
                }

                if (candidate.KnownSize is long size)
                {
                    if (size > _settings.MaxClipboardFileBytes ||
                        size > _settings.MaxClipboardFileTotalBytes - knownTotalBytes)
                    {
                        _logger.LogWarning("A clipboard file reference was skipped by configured byte limits.");
                        continue;
                    }

                    knownTotalBytes += size;
                }

                candidates.Add(candidate);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.LogWarning(exception, "A clipboard file reference could not be inspected.");
            }
        }

        var captured = new List<DropItem>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var fingerprint = FingerprintService.ForText($"clipboard-file\0{candidate.NormalizedPath}");
            var metadata = JsonSerializer.Serialize(new
            {
                batchFingerprint = snapshot.Fingerprint,
                batchItemCount = snapshot.FilePaths.Count,
                itemIndex = index,
            });
            captured.Add(await _repository.AddClipboardFileAsync(
                    candidate,
                    fingerprint,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        _logger.LogInformation(
            "Clipboard file batch committed for sequence {SequenceNumber}: offered {OfferedCount}, captured {CapturedCount}, known bytes {KnownBytes}.",
            sequenceNumber,
            snapshot.FilePaths.Count,
            captured.Count,
            knownTotalBytes);
        return captured;
    }

    private static string CreateFileClipboardFingerprint(IEnumerable<string> paths) =>
        FingerprintService.ForText(string.Join(
            '\n',
            paths.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)));

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
            state ?? (!_notifications.Status.IsRegistered
                ? ClipboardRecordingState.Error
                : _paused ? ClipboardRecordingState.Paused : ClipboardRecordingState.Recording),
            _notifications.Status.IsRegistered,
            _notifications.Status.LastNotificationUtc,
            Interlocked.Read(ref _observedEvents),
            Interlocked.Read(ref _capturedItems),
            Interlocked.Read(ref _suppressedConsecutiveDuplicates),
            Interlocked.Read(ref _failedReads),
            Interlocked.Read(ref _droppedEvents),
            message);

    private void PublishStatus(string? message, ClipboardRecordingState? state = null) =>
        StatusChanged?.Invoke(this, CreateStatus(message, state));

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private sealed record CaptureSignal(
        uint ClipboardSequenceNumber,
        long ObservedEventNumber,
        int PauseGeneration,
        DateTimeOffset ObservedAtUtc);

    private sealed record ClipboardSnapshot(
        string? Text,
        byte[]? ImageBytes,
        IReadOnlyList<string>? FilePaths,
        string Fingerprint,
        int Width,
        int Height,
        bool? HasAlpha,
        string? MimeType);
}
