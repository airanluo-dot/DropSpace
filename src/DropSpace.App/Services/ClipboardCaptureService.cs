using DropSpace.Core.Preview;
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
    private const int MaximumClipboardCopyItems = 512;

    private readonly IItemRepository _repository;
    private readonly ISettingsService _settingsService;
    private readonly IPayloadStore _payloadStore;
    private readonly IPreviewCache _previews;
    private readonly IFileReferenceService _fileReferences;
    private readonly IPayloadCleanupCoordinator? _payloadCleanup;
    private readonly ClipboardNotificationService _notifications;
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<ClipboardCaptureService> _logger;
    private readonly Channel<CaptureSignal> _signals = Channel.CreateBounded<CaptureSignal>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        SingleWriter = false,
        // Clipboard notifications are level-triggered: only the newest sequence is
        // actionable. Dropping the oldest queued signal keeps a burst from losing the
        // final clipboard state or stalling the notification thread.
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly SemaphoreSlim _clipboardWriteGate = new(1, 1);
    private readonly SemaphoreSlim _retentionGate = new(1, 1);
    private readonly ConsecutiveClipboardCaptureCoordinator _consecutiveCaptures = new();
    private Task? _worker;
    private Task? _retentionTask;
    private AppSettings _settings = new();
    private volatile bool _paused;
    private volatile bool _initialized;
    private int _pauseGeneration;
    private long _observedEvents;
    private long _capturedItems;
    private long _suppressedConsecutiveDuplicates;
    private long _failedReads;
    private long _droppedEvents;
    private long _selfWriteId;
    private DateTimeOffset _lastRetentionUtc = DateTimeOffset.MinValue;
    private uint _lastProcessedClipboardSequence;
    private int _disposeStarted;
    private readonly object _disposeGate = new();
    private readonly object _selfWriteGate = new();
    private readonly Queue<SelfWriteMarker> _selfWrites = new();
    private Task? _disposeTask;
    private Task? _lateWorkerCleanupTask;
    private int _managedResourcesDisposed;

    public ClipboardCaptureService(
        IItemRepository repository,
        ISettingsService settingsService,
        IPayloadStore payloadStore,
        IPreviewCache previews,
        IFileReferenceService fileReferences,
        ClipboardNotificationService notifications,
        DispatcherQueue dispatcher,
        IAppStringLocalizer strings,
        ILogger<ClipboardCaptureService> logger,
        IPayloadCleanupCoordinator? payloadCleanup = null)
    {
        _repository = repository;
        _settingsService = settingsService;
        _payloadStore = payloadStore;
        _previews = previews;
        _fileReferences = fileReferences;
        _payloadCleanup = payloadCleanup;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _strings = strings;
        _logger = logger;
    }

    public event EventHandler<ClipboardCaptureStatus>? StatusChanged;

    public event EventHandler<DropItem>? ItemCaptured;

    /// <summary>
    /// Raised for a remote item after it has been durably imported. This is deliberately
    /// separate from <see cref="ItemCaptured"/> so cross-device propagation can update the
    /// local UI without treating the remote event as a new local origin to broadcast.
    /// </summary>
    public event EventHandler<DropItem>? ItemImported;

    public ClipboardCaptureStatus Status => CreateStatus(null);

    public bool IsPaused => _paused;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        if (_initialized)
        {
            return;
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            if (_initialized)
            {
                return;
            }

            _settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfDisposing();
            _paused = _settings.ClipboardPaused;
            _notifications.ClipboardChanged += OnClipboardChanged;
            _notifications.StatusChanged += OnNotificationStatusChanged;
            _worker = Task.Run(ProcessSignalsAsync, CancellationToken.None);
            _retentionTask = Task.Run(RetentionLoopAsync, CancellationToken.None);
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

                _settings = await _settingsService.UpdateAsync(
                    current => current with { ClipboardPaused = true }, cancellationToken).ConfigureAwait(false);
                _paused = true;
                Interlocked.Increment(ref _pauseGeneration);
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

                _settings = await _settingsService.UpdateAsync(
                    current => current with { ClipboardPaused = false }, cancellationToken).ConfigureAwait(false);
                // Reset only after persistence succeeds. Otherwise a failed resume could
                // leave the in-memory duplicate coordinator out of sync with the still-paused
                // durable state after restart.
                // After that commit, finish the in-memory transition even if the caller
                // cancels; returning with _paused=true would contradict persisted state.
                await _consecutiveCaptures.ResetAsync(CancellationToken.None).ConfigureAwait(false);
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

    public Task ResetCaptureSequenceAsync(CancellationToken cancellationToken = default) =>
        _consecutiveCaptures.ResetAsync(cancellationToken);

    public async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = FingerprintService.ForText(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        await _clipboardWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SelfWriteMarker? selfWrite = null;
        try
        {
            await _dispatcher.EnqueueAsync(() =>
            {
                // Mark immediately before the native mutation. A busy dispatcher must not
                // consume the short-lived self-write window before Clipboard.SetContent runs.
                selfWrite = MarkSelfWrite(fingerprint);
                var package = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };
                package.SetText(text);
                return ClipboardAccessPolicy.SetContentAsync(
                    () => Clipboard.SetContent(package),
                    cancellationToken);
            }).ConfigureAwait(false);
        }
        catch
        {
            if (selfWrite is not null) ClearSelfWrite(selfWrite);
            throw;
        }
        finally { _clipboardWriteGate.Release(); }
    }

    public async Task<DropItem> ImportRemoteAsync(
        ClipboardEnvelope envelope,
        CancellationToken cancellationToken = default,
        bool publishCaptured = true)
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
                if (image.LongLength > _settings.MaxImageBytes) throw new InvalidDataException("Image encoded byte budget exceeded.");
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
                    await _payloadStore.DeleteAsync(payload.RelativePath, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }

            // The database row is durable at this point. Publish it even if PauseAsync won
            // the race after the commit, so the local UI does not remain stale merely because
            // the optional Windows clipboard side effect is skipped.
            if (publishCaptured)
            {
                PublishItemCaptured(item);
            }
            else
            {
                PublishItemImported(item);
            }

            // Keep the commit gate through the final pause check and the optional Windows
            // clipboard mutation. PauseAsync cannot become effective between this check and
            // CopyTextAsync/CopyImageAsync.
            if (_paused) return item;

            if (item.Text?.InlineText is { } text)
            {
                try
                {
                    // The history row is already durable. Do not turn a caller cancellation
                    // during the best-effort Windows clipboard side effect into a retryable
                    // import, which would create a duplicate row on the peer.
                    await CopyTextAsync(text, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The durable local history row is already committed. A clipboard mutation
                    // failure must not make the sender retry the same envelope and create a
                    // second history row; the current clipboard is simply left untouched.
                    _logger.LogWarning(exception, "Remote text was stored locally but could not be written to the Windows clipboard.");
                }
            }
            else if (item.Payload is { RelativePath: var relativePath })
            {
                try
                {
                    await CopyImageAsync(relativePath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _logger.LogWarning(exception, "Remote image was stored locally but could not be written to the Windows clipboard.");
                }
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
            var decoder = await ImageDecoderPreflight.ValidateAsync(stream, _settings.MaxImageBytes, _settings.MaxImagePixels, cancellationToken);
            return (checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight), decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore);
        });

    public async Task CopyImageAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var absolutePath = _payloadStore.ResolvePath(relativePath);
        var fileInfo = new FileInfo(absolutePath);
        if (!fileInfo.Exists || fileInfo.Length is <= 0 or > int.MaxValue || fileInfo.Length > _settings.MaxImageBytes)
        {
            throw new InvalidDataException("Clipboard image exceeds the configured copy budget.");
        }
        var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        var fingerprint = FingerprintService.ForBytes(bytes);
        await _clipboardWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SelfWriteMarker? selfWrite = null;
        try
        {
            await _dispatcher.EnqueueAsync(async () =>
            {
                selfWrite = MarkSelfWrite(fingerprint);
                var file = await StorageFile.GetFileFromPathAsync(absolutePath);
                var package = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                await ClipboardAccessPolicy.SetContentAsync(
                    () => Clipboard.SetContent(package),
                    cancellationToken);
            }).ConfigureAwait(false);
        }
        catch
        {
            if (selfWrite is not null) ClearSelfWrite(selfWrite);
            throw;
        }
        finally { _clipboardWriteGate.Release(); }
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
            .Take(MaximumClipboardCopyItems + 1)
            .ToArray();
        if (distinctPaths.Length > MaximumClipboardCopyItems)
        {
            throw new InvalidDataException(
                $"Clipboard copy supports at most {MaximumClipboardCopyItems} distinct paths.");
        }

        if (distinctPaths.Length == 0)
        {
            throw new ArgumentException("At least one file-system path is required.", nameof(paths));
        }

        var fingerprint = CreateFileClipboardFingerprint(distinctPaths);
        await _clipboardWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SelfWriteMarker? selfWrite = null;
        try
        {
            await _dispatcher.EnqueueAsync(async () =>
            {
                selfWrite = MarkSelfWrite(fingerprint);
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
                await ClipboardAccessPolicy.SetContentAsync(
                    () => Clipboard.SetContent(package),
                    cancellationToken);
            }).ConfigureAwait(false);
        }
        catch
        {
            if (selfWrite is not null) ClearSelfWrite(selfWrite);
            throw;
        }
        finally { _clipboardWriteGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
        }

        await _disposeTask!.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeStarted, 1);
        // Initialization may still be awaiting settings. Keep its gate alive until
        // it has observed shutdown and released ownership of initialization.
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                _notifications.ClipboardChanged -= OnClipboardChanged;
                _notifications.StatusChanged -= OnNotificationStatusChanged;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        _signals.Writer.TryComplete();
        _shutdown.Cancel();
        var worker = _worker;
        var workerCompleted = worker is null;
        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                workerCompleted = true;
            }
            catch (TimeoutException)
            {
                // The worker may still be inside a UI/COM call. Its gates and CTS remain
                // owned until that task actually exits; disposing them here would create a
                // use-after-dispose race during a stalled shutdown.
                _logger.LogInformation("Clipboard worker shutdown exceeded the bounded wait; managed resources remain owned by the worker.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Clipboard worker shutdown was cancelled.");
                workerCompleted = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(exception, "Clipboard worker failed while shutting down.");
                workerCompleted = true;
            }
        }

        var retentionCompleted = _retentionTask is null;
        if (_retentionTask is not null)
        {
            try
            {
                await _retentionTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                retentionCompleted = true;
            }
            catch (TimeoutException) { _logger.LogInformation("Clipboard retention worker shutdown exceeded the bounded wait."); }
            catch (OperationCanceledException) { retentionCompleted = true; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(exception, "Clipboard retention worker failed while shutting down.");
                retentionCompleted = true;
            }
        }

        if (workerCompleted && retentionCompleted)
        {
            DisposeManagedResources();
        }
        else
        {
            _lateWorkerCleanupTask = DisposeResourcesAfterBackgroundTasksAsync(worker, _retentionTask);
        }
    }

    private async Task DisposeResourcesAfterBackgroundTasksAsync(Task? worker, Task? retentionTask)
    {
        try
        {
            var tasks = new[] { worker, retentionTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogDebug(exception, "Clipboard background task completed after the bounded shutdown wait.");
        }
        finally
        {
            DisposeManagedResources();
        }
    }

    private void DisposeManagedResources()
    {
        if (Interlocked.Exchange(ref _managedResourcesDisposed, 1) != 0)
        {
            return;
        }

        _shutdown.Dispose();
        _stateGate.Dispose();
        _commitGate.Dispose();
        _clipboardWriteGate.Dispose();
        _retentionGate.Dispose();
        _consecutiveCaptures.Dispose();
    }

    private void OnClipboardChanged(object? sender, ClipboardNotification notification)
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

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

    private bool IsSignalStillCurrent(CaptureSignal signal)
    {
        if (signal.ClipboardSequenceNumber == 0) return true;
        var current = GetClipboardSequenceNumber();
        return current == 0 || current == signal.ClipboardSequenceNumber;
    }

    private void QueueCurrentClipboardSignal(CaptureSignal signal)
    {
        var current = GetClipboardSequenceNumber();
        if (current == 0 || current == signal.ClipboardSequenceNumber) return;
        _signals.Writer.TryWrite(new CaptureSignal(
            current,
            Interlocked.Read(ref _observedEvents),
            Volatile.Read(ref _pauseGeneration),
            DateTimeOffset.UtcNow));
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
                    if (snapshot is null)
                    {
                        // A retry can discover that a newer clipboard sequence replaced the
                        // signal while the read was in flight. Do not consume the old signal
                        // without giving the newer sequence a chance to be processed.
                        QueueCurrentClipboardSignal(signal);
                        _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                        continue;
                    }

                    if (_paused || signal.PauseGeneration != Volatile.Read(ref _pauseGeneration))
                    {
                        continue;
                    }

                    // A queued signal may be older than the clipboard contents that are now
                    // being read. Never commit the newer contents under the old sequence; the
                    // latest sequence is queued again below and will be read as its own event.
                    if (!IsSignalStillCurrent(signal))
                    {
                        QueueCurrentClipboardSignal(signal);
                        _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                        continue;
                    }

                    if (IsSelfWrite(snapshot.Fingerprint))
                    {
                        _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                        _logger.LogInformation(
                            "Clipboard self-write suppressed for sequence {SequenceNumber}.",
                            signal.ClipboardSequenceNumber);
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

                        if (!IsSignalStillCurrent(signal))
                        {
                            QueueCurrentClipboardSignal(signal);
                            _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
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
                            _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                            _logger.LogInformation(
                                "Consecutive clipboard snapshot suppressed for sequence {SequenceNumber}.",
                                signal.ClipboardSequenceNumber);
                            PublishStatus(null);
                            continue;
                        }

                        items = capture.Value;
                        if (items.Count == 0)
                        {
                            _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
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
                        PublishItemCaptured(item);
                    }
                    _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                    PublishStatus(null);
                    try
                    {
                        await ApplyRetentionIfDueAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception retentionException) when (retentionException is not OutOfMemoryException)
                    {
                        // Retention is maintenance. A failed cleanup must not make the
                        // already-committed clipboard signal look uncommitted and trigger a
                        // duplicate retry of the user's content.
                        _logger.LogWarning(retentionException, "Clipboard retention pass failed after a successful capture.");
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Clipboard event could not be captured.");
                    if (!_shutdown.IsCancellationRequested && signal.Attempt < 2)
                    {
                        var currentSequence = GetClipboardSequenceNumber();
                        if (signal.ClipboardSequenceNumber == 0 ||
                            currentSequence == 0 ||
                            currentSequence == signal.ClipboardSequenceNumber)
                        {
                            if (_signals.Writer.TryWrite(signal with { Attempt = signal.Attempt + 1 }))
                            {
                                PublishStatus(_strings.Get("ClipboardBusyRetrying"));
                                continue;
                            }

                            Interlocked.Increment(ref _droppedEvents);
                        }
                    }

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

    private async Task<ClipboardSnapshot?> ReadSnapshotAsync(
        CaptureSignal signal,
        CancellationToken cancellationToken)
    {
        var source = await _dispatcher.EnqueueAsync(
                () => ReadClipboardSnapshotSourceAsync(signal, cancellationToken))
            .ConfigureAwait(false);
        if (source.Snapshot is not null)
        {
            return source.Snapshot;
        }

        var imageBytes = source.ImageBytes;
        if (imageBytes is null)
        {
            return null;
        }

        try
        {
            // Clipboard access and the initial byte copy stay on the UI dispatcher. Decode,
            // dimension validation, and PNG encoding continue on the worker after the dispatcher
            // task completes so large-image work does not occupy the UI thread.
            return await EncodeClipboardImageAsync(
                    imageBytes,
                    signal,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(imageBytes);
        }
    }

    private async Task<ClipboardReadResult> ReadClipboardSnapshotSourceAsync(
        CaptureSignal signal,
        CancellationToken cancellationToken)
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
                .Take(_settings.MaxClipboardFileItems + 1)
                .ToArray();
            if (paths.Length > 0)
            {
                return new ClipboardReadResult(
                    new ClipboardSnapshot(
                        null,
                        null,
                        paths,
                        CreateFileClipboardFingerprint(paths),
                        0,
                        0,
                        null,
                        null),
                    null);
            }

            // A producer can publish the StorageItems format before its async item payload is
            // materialized. Treat that empty read as transient; otherwise this WM_CLIPBOARDUPDATE
            // would be marked processed and the file/folder batch could never be captured.
            throw new COMException(
                "Clipboard storage items are not ready.",
                unchecked((int)0x8000000A)); // E_PENDING
        }

        if (view.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await view.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 ||
                stream.Size > (ulong)_settings.MaxImageBytes ||
                stream.Size > int.MaxValue)
            {
                return new ClipboardReadResult(
                    CreateRejectedSnapshot(signal, "image-byte-limit"),
                    null);
            }

            var bytes = new byte[checked((int)stream.Size)];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(checked((uint)bytes.Length));
            reader.ReadBytes(bytes);
            return new ClipboardReadResult(null, bytes);
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            var text = await view.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ClipboardReadResult(
                    CreateRejectedSnapshot(signal, "empty-text"),
                    null);
            }

            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            return new ClipboardReadResult(
                new ClipboardSnapshot(
                    normalized,
                    null,
                    null,
                    FingerprintService.ForText(normalized),
                    0,
                    0,
                    null,
                    null),
                null);
        }

        return new ClipboardReadResult(
            CreateRejectedSnapshot(signal, "unsupported-format"),
            null);
    }

    private async Task<ClipboardSnapshot> EncodeClipboardImageAsync(
        byte[] sourceBytes,
        CaptureSignal signal,
        CancellationToken cancellationToken)
    {
        var budget = ClipboardImageBudgetPolicy.Create(
            _settings.MaxImageBytes,
            _settings.MaxImagePixels);

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(sourceBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var sourceAssessment = budget.Assess(
            sourceBytes.LongLength,
            decoder.PixelWidth,
            decoder.PixelHeight);
        if (!sourceAssessment.IsWithinBudget)
        {
            return CreateRejectedSnapshot(signal, sourceAssessment.ErrorCategory ?? "image-budget-limit");
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        using var encodedStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId,
            encodedStream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        if (encodedStream.Size == 0 || encodedStream.Size > int.MaxValue)
        {
            return CreateRejectedSnapshot(signal, "encoded-image-byte-limit");
        }

        var encodedAssessment = budget.Assess(
            checked((long)encodedStream.Size),
            decoder.PixelWidth,
            decoder.PixelHeight);
        if (!encodedAssessment.IsWithinBudget)
        {
            return CreateRejectedSnapshot(signal, "encoded-image-budget-limit");
        }

        var bytes = new byte[checked((int)encodedStream.Size)];
        using var reader = new DataReader(encodedStream.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)bytes.Length));
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
                    await _payloadStore.DeleteAsync(payload.RelativePath, CancellationToken.None).ConfigureAwait(false);
                }

                _logger.LogInformation("Clipboard image committed for sequence {SequenceNumber}.", sequenceNumber);
                return [item];
            }
            catch
            {
                await _payloadStore.DeleteAsync(payload.RelativePath, CancellationToken.None).ConfigureAwait(false);
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

        // Keep clipboard file batches on the same metadata contract as explicit file
        // drops. The previous ad-hoc JSON (batchFingerprint/batchItemCount/itemIndex)
        // could be stored successfully but could never deserialize as
        // DropBatchMetadata, so the UI lost the batch header and expand/collapse state.
        var batchId = Guid.NewGuid();
        var batchItemCount = candidates.Count;
        var batchCandidates = candidates.Select((candidate, index) =>
                     new ClipboardFileCandidate(
                         candidate,
                         FingerprintService.ForText($"clipboard-file\0{candidate.NormalizedPath}"),
                         JsonSerializer.Serialize(new DropBatchMetadata(
                             batchId,
                             null,
                             index,
                             batchItemCount,
                             "clipboard-files"))))
            .ToArray();

        var captured = await _repository.AddClipboardFilesAsync(batchCandidates, cancellationToken).ConfigureAwait(false);

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
        await _retentionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastRetentionUtc < TimeSpan.FromMinutes(5))
            {
                return;
            }

            var result = await _repository.ApplyRetentionAsync(
                    now.AddDays(-_settings.RetentionDays),
                    _settings.RetentionItemCount,
                    cancellationToken)
                    .ConfigureAwait(false);
            if (result.RemovedCount > 0)
                await _previews.ClearAsync(cancellationToken).ConfigureAwait(false);
            if (_payloadCleanup is not null)
            {
                // Retention deletes payload rows transactionally and records their physical
                // paths in the durable outbox. Drain that outbox so successful deletion also
                // completes the obligation; otherwise every retention pass would leave a stale
                // entry until the next process restart. Failed entries remain retryable.
                while (await _payloadCleanup.DrainAsync(cancellationToken).ConfigureAwait(false) > 0) { }
            }
            else
            {
                foreach (var path in result.PayloadPaths)
                {
                    try
                    {
                        await _payloadStore.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(exception, "Deferred payload cleanup failed.");
                    }
                }
            }

            // Advance the watermark only after the database and preview cleanup completed;
            // a failed pass is then eligible for a retry instead of being suppressed for five
            // minutes.
            _lastRetentionUtc = now;
        }
        finally
        {
            _retentionGate.Release();
        }
    }

    private SelfWriteMarker MarkSelfWrite(string fingerprint)
    {
        var marker = new SelfWriteMarker(
            Interlocked.Increment(ref _selfWriteId),
            fingerprint,
            DateTimeOffset.UtcNow.AddSeconds(3));
        lock (_selfWriteGate)
        {
            PruneSelfWrites(DateTimeOffset.UtcNow);
            _selfWrites.Enqueue(marker);
        }

        return marker;
    }

    private bool IsSelfWrite(string fingerprint)
    {
        lock (_selfWriteGate)
        {
            PruneSelfWrites(DateTimeOffset.UtcNow);
            var matched = false;
            var remaining = new Queue<SelfWriteMarker>(_selfWrites.Count);
            while (_selfWrites.TryDequeue(out var marker))
            {
                if (!matched && string.Equals(fingerprint, marker.Fingerprint, StringComparison.Ordinal))
                {
                    matched = true;
                    continue;
                }

                remaining.Enqueue(marker);
            }

            while (remaining.TryDequeue(out var marker)) _selfWrites.Enqueue(marker);
            return matched;
        }
    }

    private void ClearSelfWrite(SelfWriteMarker marker)
    {
        lock (_selfWriteGate)
        {
            var remaining = new Queue<SelfWriteMarker>(_selfWrites.Count);
            while (_selfWrites.TryDequeue(out var current))
            {
                if (current.Id != marker.Id) remaining.Enqueue(current);
            }

            while (remaining.TryDequeue(out var current)) _selfWrites.Enqueue(current);
        }
    }

    private void PruneSelfWrites(DateTimeOffset now)
    {
        while (_selfWrites.TryPeek(out var marker) && marker.ExpiresUtc < now) _selfWrites.Dequeue();
    }

    private async Task RetentionLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await ApplyRetentionIfDueAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _logger.LogWarning(exception, "Clipboard retention pass failed; it will be retried.");
                }

                if (!await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void PublishItemCaptured(DropItem item)
    {
        if (ItemCaptured is not { } handlers) return;
        foreach (EventHandler<DropItem> handler in handlers.GetInvocationList())
        {
            try { handler(this, item); }
            catch (Exception exception) { _logger.LogWarning(exception, "Clipboard item subscriber failed ({Category}).", exception.GetType().Name); }
        }
    }

    private void PublishItemImported(DropItem item)
    {
        if (ItemImported is not { } handlers) return;
        foreach (EventHandler<DropItem> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, item);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(exception, "Clipboard import subscriber failed without changing the imported item.");
            }
        }
    }

    private void PublishStatus(string? message, ClipboardRecordingState? state = null)
    {
        if (StatusChanged is not { } handlers) return;
        var status = CreateStatus(message, state);
        foreach (EventHandler<ClipboardCaptureStatus> handler in handlers.GetInvocationList())
        {
            try { handler(this, status); }
            catch (Exception exception) { _logger.LogWarning(exception, "Clipboard status subscriber failed ({Category}).", exception.GetType().Name); }
        }
    }

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

    private void ThrowIfDisposing() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private sealed record CaptureSignal(
        uint ClipboardSequenceNumber,
        long ObservedEventNumber,
        int PauseGeneration,
        DateTimeOffset ObservedAtUtc,
        int Attempt = 0);

    private sealed record SelfWriteMarker(long Id, string Fingerprint, DateTimeOffset ExpiresUtc);

    private sealed record ClipboardReadResult(
        ClipboardSnapshot? Snapshot,
        byte[]? ImageBytes);

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
