using DropSpace.Core.Preview;
using System.Threading.Channels;
using System.Collections.Concurrent;
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
using Windows.Foundation;
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
    string? Message,
    AppUiMessage? MessageIdentity = null);

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
    private readonly Channel<CaptureSignal> _signals;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly SemaphoreSlim _clipboardWriteGate = new(1, 1);
    private readonly SemaphoreSlim _retentionGate = new(1, 1);
    private readonly ConsecutiveClipboardCaptureCoordinator _consecutiveCaptures = new();
    private readonly TextReadCoordinator _textReads = new();
    private readonly TextReadCoordinator _providerReads = new();
    private readonly SemaphoreSlim _providerReadSlots = new(8, 8);
    private readonly ConcurrentDictionary<long, Task> _retiredProviderReads = new();
    private long _providerReadId;
    // Count active and retired native operations through Cancel and Close.
    private readonly SemaphoreSlim _textReadSlots = new(8, 8);
    private readonly ConcurrentDictionary<long, Task> _retiredTextReads = new();
    private long _retiredTextReadId;
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
    private readonly ClipboardDiagnosticTrace _diagnostics = new();
    private long _diagnosticReadAttempts;
    private long _diagnosticEnqueuedSignals;
    private long _diagnosticDequeuedSignals;
    private uint _lastDiagnosticNotificationSequence;

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
        _signals = Channel.CreateBounded<CaptureSignal>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            // Notifications are level-triggered; keep the latest sequence without
            // blocking the listener, while counting every evicted older signal.
            FullMode = BoundedChannelFullMode.DropOldest,
        }, signal =>
        {
            Interlocked.Increment(ref _droppedEvents);
            RecordDiagnostic(ClipboardDiagnosticDecision.SignalDropped, signal);
        });
    }

    // Internal initialization seam for deterministic unavailable-view native tests.
    // Production always reads on the existing OLE/dispatcher owner.
    internal Func<DataPackageView> ClipboardViewReader { get; init; } = Clipboard.GetContent;

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

    internal void BeginDiagnosticSession()
    {
        Interlocked.Exchange(ref _diagnosticReadAttempts, 0);
        Interlocked.Exchange(ref _diagnosticEnqueuedSignals, 0);
        Interlocked.Exchange(ref _diagnosticDequeuedSignals, 0);
        Volatile.Write(ref _lastDiagnosticNotificationSequence, 0);
        _diagnostics.Enable();
    }

    internal void EndDiagnosticSession() => _diagnostics.Disable();

    internal void BeginDiagnosticStage(ClipboardSmokeStage stage,
        ClipboardSmokeProfile profile = ClipboardSmokeProfile.Baseline, int cycle = 0) =>
        _diagnostics.BeginStage(stage, CreateDiagnosticState(), profile, cycle);

    internal ClipboardDiagnosticState DiagnosticCurrentState => CreateDiagnosticState();

    internal ClipboardDiagnosticDocument DiagnosticSnapshot => _diagnostics.Snapshot(CreateDiagnosticState());

    internal void RecordSmokeDiagnostic(ClipboardDiagnosticDecision decision, Exception? exception = null) =>
        _diagnostics.Record(decision, CreateDiagnosticState(), failure: ClipboardDiagnosticTrace.Classify(exception), hResult: exception?.HResult);

    private ClipboardDiagnosticState CreateDiagnosticState()
    {
        uint? nativeSequence = null;
        try { nativeSequence = GetClipboardSequenceNumber(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        return new(DateTimeOffset.UtcNow, nativeSequence,
            Volatile.Read(ref _lastDiagnosticNotificationSequence), Volatile.Read(ref _lastProcessedClipboardSequence),
            new(Interlocked.Read(ref _observedEvents), Interlocked.Read(ref _diagnosticReadAttempts),
                Interlocked.Read(ref _capturedItems), Interlocked.Read(ref _suppressedConsecutiveDuplicates),
                Interlocked.Read(ref _failedReads), Interlocked.Read(ref _droppedEvents),
                Interlocked.Read(ref _diagnosticEnqueuedSignals), Interlocked.Read(ref _diagnosticDequeuedSignals),
                8 - _textReadSlots.CurrentCount, _retiredTextReads.Count),
            _notifications?.Status.IsRegistered ?? false, _paused, Volatile.Read(ref _pauseGeneration),
            _initialized, Volatile.Read(ref _disposeStarted) != 0, _worker?.Status);
    }

    private void RecordDiagnostic(ClipboardDiagnosticDecision decision, CaptureSignal? signal = null, Exception? exception = null, int? readAttempt = null, int? formatCount = null)
    {
        if (!_diagnostics.IsEnabled) return;
        _diagnostics.Record(decision, CreateDiagnosticState(), signal?.ClipboardSequenceNumber, signal?.Attempt,
            readAttempt, ClipboardDiagnosticTrace.Classify(exception), exception?.HResult, formatCount);
    }

    private bool TryQueueSignal(CaptureSignal signal)
    {
        var accepted = _signals.Writer.TryWrite(signal);
        if (_diagnostics.IsEnabled)
        {
            if (accepted) Interlocked.Increment(ref _diagnosticEnqueuedSignals);
            RecordDiagnostic(accepted ? ClipboardDiagnosticDecision.SignalQueued : ClipboardDiagnosticDecision.SignalDropped, signal);
        }
        return accepted;
    }

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
                    ? _paused ? AppUiMessage.Resource("ClipboardPausedAtStartup") : null
                    : AppUiMessage.Resource("ClipboardListenerRegistrationFailed"),
                _notifications.Status.IsRegistered ? null : ClipboardRecordingState.Error);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        // Serialize the transition with repository commits. Taking commitGate first
        // means a pause request either precedes a remote import or waits for that
        // already-started import; no later import can pass the check while paused.
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposing();
                if (_paused)
                {
                    return;
                }

                _settings = await _settingsService.UpdateAsync(
                    current => current with { ClipboardPaused = true }, cancellationToken).ConfigureAwait(false);
                _paused = true;
                Interlocked.Increment(ref _pauseGeneration);
                PublishStatus(AppUiMessage.Resource("ClipboardPaused"));
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
        ThrowIfDisposing();
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposing();
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
                PublishStatus(AppUiMessage.Resource("ClipboardResumed"));
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
        ThrowIfDisposing();
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            _settings = settings;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public Task ResetCaptureSequenceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        return _consecutiveCaptures.ResetAsync(cancellationToken);
    }

    public async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = FingerprintService.ForText(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        await _clipboardWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SelfWriteMarker? selfWrite = null;
        try
        {
            ThrowIfDisposing();
            await _dispatcher.EnqueueAsync(() =>
            {
                ThrowIfDisposing();
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
        ThrowIfDisposing();
        if (_paused) throw new ClipboardPausedException();
        ClipboardEnvelopePolicy.Validate(envelope);
        DropItem item;
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
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
                var payload = await _payloadStore.WriteFileAsync("images", dimensions.Extension, stream,
                    _settings.MaxImageBytes, cancellationToken).ConfigureAwait(false);
                try
                {
                    item = await _repository.AddImageAsync(
                        new ImageCandidate(envelope.Sha256, dimensions.Width, dimensions.Height, image.LongLength, dimensions.MimeType, dimensions.HasAlpha, payload),
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

    private Task<(int Width, int Height, bool HasAlpha, string Extension, string MimeType)> ReadImageDimensionsAsync(byte[] bytes, CancellationToken cancellationToken) =>
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
            // The peer's MIME label is not a file-format authority. Keep the original
            // validated bytes and derive both their extension and MIME from the decoder.
            var codec = decoder.DecoderInformation;
            var extension = codec.FileExtensions.FirstOrDefault(value => value.Length is > 1 and <= 16 &&
                value[0] == '.' && value.AsSpan(1).ToArray().All(char.IsAsciiLetterOrDigit))
                ?? throw new InvalidDataException("The clipboard image decoder has no safe file extension.");
            var mimeType = codec.MimeTypes.FirstOrDefault(value => value.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The clipboard image decoder has no image MIME type.");
            return (checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight),
                decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore, extension, mimeType);
        });

    public async Task CopyImageAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
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
            ThrowIfDisposing();
            await _dispatcher.EnqueueAsync(async () =>
            {
                ThrowIfDisposing();
                var file = await StorageFile.GetFileFromPathAsync(absolutePath);
                var package = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };
                package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                selfWrite = MarkSelfWrite(fingerprint);
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
        ThrowIfDisposing();
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
            ThrowIfDisposing();
            await _dispatcher.EnqueueAsync(async () =>
            {
                ThrowIfDisposing();
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
                // Resolving network paths can outlast the short self-write window.
                // Start that window only when the prepared package is ready to write.
                selfWrite = MarkSelfWrite(fingerprint);
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
        // Public operations may already own or await these managed gates while
        // their UI/COM work finishes. Keep them usable for final Release and for
        // queued callers to observe shutdown after acquiring ownership. None of
        // these semaphores creates a native wait handle in this service.
    }

    private void OnClipboardChanged(object? sender, ClipboardNotification notification)
    {
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        var observed = Interlocked.Increment(ref _observedEvents);
        if (_diagnostics.IsEnabled) Volatile.Write(ref _lastDiagnosticNotificationSequence, notification.SequenceNumber);
        RecordDiagnostic(ClipboardDiagnosticDecision.Notification);
        if (_paused)
        {
            RecordDiagnostic(ClipboardDiagnosticDecision.PausedSignal);
            PublishStatus(null);
            return;
        }

        var signal = new CaptureSignal(
            notification.SequenceNumber,
            observed,
            Volatile.Read(ref _pauseGeneration),
            notification.ObservedAtUtc);
        if (!TryQueueSignal(signal))
        {
            Interlocked.Increment(ref _droppedEvents);
            PublishStatus(AppUiMessage.Resource("ClipboardEventDropped"));
        }
        else if (_textReads.HasActiveRead || _providerReads.HasActiveRead)
        {
            // Admit the notification before releasing an older text waiter. The
            // actual sequence can already be newer than the sampled notification.
            var currentSequence = GetClipboardSequenceNumber();
            if (_textReads.HasActiveRead)
                _textReads.Notify(currentSequence, signal.ClipboardSequenceNumber, QueueTextSequence);
            if (_providerReads.HasActiveRead)
                _providerReads.Notify(currentSequence, signal.ClipboardSequenceNumber, QueueTextSequence);
        }
    }

    private bool QueueTextSequence(uint sequence) => TryQueueSignal(new CaptureSignal(
        sequence, Interlocked.Read(ref _observedEvents), Volatile.Read(ref _pauseGeneration), DateTimeOffset.UtcNow));

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
        TryQueueSignal(new CaptureSignal(
            current,
            Interlocked.Read(ref _observedEvents),
            Volatile.Read(ref _pauseGeneration),
            DateTimeOffset.UtcNow));
    }

    private void OnNotificationStatusChanged(object? sender, ClipboardNotificationStatus status)
    {
        if (!status.IsRegistered)
        {
            PublishStatus(AppUiMessage.Resource("ClipboardListenerUnavailable"), ClipboardRecordingState.Error);
        }
    }

    private async Task ProcessSignalsAsync()
    {
        RecordDiagnostic(ClipboardDiagnosticDecision.WorkerStarted);
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                if (_diagnostics.IsEnabled) Interlocked.Increment(ref _diagnosticDequeuedSignals);
                RecordDiagnostic(ClipboardDiagnosticDecision.SignalDequeued, signal);
                if (_paused || signal.PauseGeneration != Volatile.Read(ref _pauseGeneration))
                {
                    RecordDiagnostic(ClipboardDiagnosticDecision.PausedSignal, signal);
                    continue;
                }

                if (signal.ClipboardSequenceNumber != 0 &&
                    signal.ClipboardSequenceNumber == _lastProcessedClipboardSequence)
                {
                    RecordDiagnostic(ClipboardDiagnosticDecision.DuplicateSequence, signal);
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
                        RecordDiagnostic(ClipboardDiagnosticDecision.SnapshotUnavailable, signal);
                        // A retry can discover that a newer clipboard sequence replaced the
                        // signal while the read was in flight. Do not consume the old signal
                        // without giving the newer sequence a chance to be processed.
                        QueueCurrentClipboardSignal(signal);
                        _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                        RecordDiagnostic(ClipboardDiagnosticDecision.SequenceConsumed, signal);
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
                        RecordDiagnostic(ClipboardDiagnosticDecision.StaleSequence, signal);
                        QueueCurrentClipboardSignal(signal);
                        _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                        RecordDiagnostic(ClipboardDiagnosticDecision.SequenceConsumed, signal);
                        continue;
                    }

                    if (IsSelfWrite(snapshot.Fingerprint))
                    {
                        RecordDiagnostic(ClipboardDiagnosticDecision.SelfWriteSuppressed, signal);
                        _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                        RecordDiagnostic(ClipboardDiagnosticDecision.SequenceConsumed, signal);
                        _logger.LogInformation(
                            "Clipboard self-write suppressed for sequence {SequenceNumber}.",
                            signal.ClipboardSequenceNumber);
                        continue;
                    }

                    IReadOnlyList<DropItem> items;
                    RecordDiagnostic(ClipboardDiagnosticDecision.CommitGateWaiting, signal);
                    await _commitGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    try
                    {
                        RecordDiagnostic(ClipboardDiagnosticDecision.CommitGateAcquired, signal);
                        if (_paused || signal.PauseGeneration != Volatile.Read(ref _pauseGeneration))
                        {
                            continue;
                        }

                        if (!IsSignalStillCurrent(signal))
                        {
                            RecordDiagnostic(ClipboardDiagnosticDecision.StaleSequence, signal);
                            QueueCurrentClipboardSignal(signal);
                            _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                            RecordDiagnostic(ClipboardDiagnosticDecision.SequenceConsumed, signal);
                            continue;
                        }

                        var capture = await _consecutiveCaptures.ExecuteAsync(
                                snapshot.Fingerprint,
                                token => CommitSnapshotWithDiagnosticsAsync(snapshot, signal, token),
                                committedItems => committedItems.Count > 0,
                                _shutdown.Token)
                            .ConfigureAwait(false);
                        if (capture.Suppressed)
                        {
                            Interlocked.Increment(ref _suppressedConsecutiveDuplicates);
                            _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                            RecordDiagnostic(ClipboardDiagnosticDecision.SequenceConsumed, signal);
                            RecordDiagnostic(ClipboardDiagnosticDecision.ConsecutiveSuppressed, signal);
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
                            RecordDiagnostic(ClipboardDiagnosticDecision.SequenceConsumed, signal);
                            RecordDiagnostic(ClipboardDiagnosticDecision.NoItemsCommitted, signal);
                            continue;
                        }
                    }
                    finally
                    {
                        _commitGate.Release();
                    }

                    Interlocked.Add(ref _capturedItems, items.Count);
                    RecordDiagnostic(ClipboardDiagnosticDecision.ItemsCaptured, signal);
                    foreach (var item in items)
                    {
                        PublishItemCaptured(item);
                    }
                    _lastProcessedClipboardSequence = signal.ClipboardSequenceNumber;
                    RecordDiagnostic(ClipboardDiagnosticDecision.SequenceConsumed, signal);
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
                    RecordDiagnostic(ClipboardDiagnosticDecision.CaptureFailed, signal, exception);
                    _logger.LogWarning(exception, "Clipboard event could not be captured.");
                    if (!_shutdown.IsCancellationRequested && signal.Attempt < 2)
                    {
                        var currentSequence = GetClipboardSequenceNumber();
                        if (signal.ClipboardSequenceNumber == 0 ||
                            currentSequence == 0 ||
                            currentSequence == signal.ClipboardSequenceNumber)
                        {
                            if (TryQueueSignal(signal with { Attempt = signal.Attempt + 1 }))
                            {
                                PublishStatus(AppUiMessage.Resource("ClipboardBusyRetrying"));
                                continue;
                            }

                            Interlocked.Increment(ref _droppedEvents);
                        }
                    }

                    PublishStatus(AppUiMessage.Resource("ClipboardItemCaptureFailed"));
                }
            }
        }
        catch (OperationCanceledException)
        {
            RecordDiagnostic(ClipboardDiagnosticDecision.WorkerCancelled);
            return;
        }
        catch (Exception exception)
        {
            RecordDiagnostic(ClipboardDiagnosticDecision.WorkerFailed, exception: exception);
            _logger.LogError(exception, "Clipboard capture worker stopped unexpectedly.");
            PublishStatus(AppUiMessage.Resource("ClipboardCaptureStopped"), ClipboardRecordingState.Error);
        }
        finally { RecordDiagnostic(ClipboardDiagnosticDecision.WorkerCompleted); }
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
                cancellationToken.ThrowIfCancellationRequested();
                var currentSequence = GetClipboardSequenceNumber();
                if (signal.ClipboardSequenceNumber != 0 &&
                    currentSequence != 0 &&
                    currentSequence != signal.ClipboardSequenceNumber)
                {
                    RecordDiagnostic(ClipboardDiagnosticDecision.RetrySequenceAdvanced, signal);
                    _logger.LogInformation(
                        "Clipboard retry abandoned because sequence advanced from {OriginalSequence} to {CurrentSequence}.",
                        signal.ClipboardSequenceNumber,
                        currentSequence);
                    return null;
                }
            }

            try
            {
                if (_diagnostics.IsEnabled) Interlocked.Increment(ref _diagnosticReadAttempts);
                RecordDiagnostic(ClipboardDiagnosticDecision.ReadAttempt, signal, readAttempt: attempt + 1);
                _logger.LogInformation(
                    "Clipboard snapshot read started for sequence {SequenceNumber}, attempt {Attempt}.",
                    signal.ClipboardSequenceNumber,
                    attempt + 1);
                var snapshot = await ReadSnapshotAsync(signal, cancellationToken, retryEmptyView: attempt < 2).ConfigureAwait(false);
                RecordDiagnostic(ClipboardDiagnosticDecision.ReadCompleted, signal);
                _logger.LogInformation(
                    "Clipboard snapshot read completed for sequence {SequenceNumber}; format {Format}.",
                    signal.ClipboardSequenceNumber,
                    snapshot?.FilePaths is not null ? "files" : snapshot?.Text is not null ? "text" : snapshot?.ImageBytes is not null ? "image" : "unsupported");
                return snapshot;
            }
            catch (EmptyClipboardViewException)
            {
                // GetContent can temporarily expose no formats without throwing.
                // Only the first two attempts retry this state; a still-empty third
                // view is consumed normally, without a permanent busy/error status.
            }
            catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
            {
                lastException = exception;
                Interlocked.Increment(ref _failedReads);
                RecordDiagnostic(ClipboardDiagnosticDecision.ReadFailed, signal, exception, attempt + 1);
                PublishStatus(AppUiMessage.Resource("ClipboardBusyRetrying"));
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
        CancellationToken cancellationToken,
        bool retryEmptyView)
    {
        RecordDiagnostic(ClipboardDiagnosticDecision.DispatcherQueued, signal);
        var source = await _dispatcher.EnqueueAsync(
                () => ReadClipboardSnapshotSourceAsync(signal, cancellationToken, retryEmptyView))
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
        CancellationToken cancellationToken,
        bool retryEmptyView)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordDiagnostic(ClipboardDiagnosticDecision.ClipboardViewReadStarted, signal);
        var view = ClipboardViewReader();
        RecordDiagnostic(ClipboardDiagnosticDecision.ClipboardViewRead, signal);

        if (view.Contains(StandardDataFormats.StorageItems))
            return await ReadDeferredProviderAsync(signal, async token =>
            {
                RecordDiagnostic(ClipboardDiagnosticDecision.StorageItemsRead, signal);
                var storageItems = await ReadProviderOperationAsync(view.GetStorageItemsAsync, token);
                token.ThrowIfCancellationRequested();
                var paths = storageItems.Select(item => item.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(_settings.MaxClipboardFileItems + 1).ToArray();
                if (paths.Length > 0)
                    return new ClipboardReadResult(new ClipboardSnapshot(null, null, paths,
                        CreateFileClipboardFingerprint(paths), 0, 0, null, null), null);
                // A producer can announce StorageItems before its payload is ready.
                throw new COMException("Clipboard storage items are not ready.", unchecked((int)0x8000000A));
            }, cancellationToken);

        if (view.Contains(StandardDataFormats.Bitmap))
            return await ReadDeferredProviderAsync(signal, async token =>
            {
                RecordDiagnostic(ClipboardDiagnosticDecision.BitmapRead, signal);
                var reference = await ReadProviderOperationAsync(view.GetBitmapAsync, token);
                token.ThrowIfCancellationRequested();
                using var stream = await ReadProviderOperationAsync(reference.OpenReadAsync, token);
                token.ThrowIfCancellationRequested();
                if (stream.Size == 0 || stream.Size > (ulong)_settings.MaxImageBytes || stream.Size > int.MaxValue)
                    return new ClipboardReadResult(CreateRejectedSnapshot(signal, "image-byte-limit"), null);
                var bytes = new byte[checked((int)stream.Size)];
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                try
                {
                    var loaded = await ReadProviderOperationAsync(() => reader.LoadAsync(checked((uint)bytes.Length)), token);
                    token.ThrowIfCancellationRequested();
                    if (loaded != bytes.Length) throw new InvalidDataException("The clipboard bitmap ended before its declared length.");
                    reader.ReadBytes(bytes);
                    return new ClipboardReadResult(null, bytes);
                }
                catch { Array.Clear(bytes); throw; }
            }, cancellationToken);

        if (view.Contains(StandardDataFormats.Text))
        {
            // Retired native reads may never return; a full text pool must not block
            // the single signal consumer from reaching newer image/file updates.
            if (!await ClipboardProviderReadLifetime.TryReserveSlotAsync(_textReadSlots, cancellationToken).ConfigureAwait(false))
                return new ClipboardReadResult(CreateRejectedSnapshot(signal, "text-read-capacity"), null);
            IAsyncOperation<string>? operation = null;
            Task<string>? nativeRead = null;
            TextReadLease? read = null;
            string text;
            try
            {
                // A dedicated queue may have no managed synchronization context.
                // Return explicitly to the owner after a pending slot acquisition.
                if (_dispatcher.HasThreadAccess) await StartReadAsync();
                else await _dispatcher.EnqueueAsync(StartReadAsync).ConfigureAwait(false);
                if (nativeRead is null || read is null) return new ClipboardReadResult(null, null);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, read.SupersededToken);
                try
                {
                    text = await nativeRead.WaitAsync(stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (read.IsSuperseded && !cancellationToken.IsCancellationRequested)
                {
                    RecordDiagnostic(ClipboardDiagnosticDecision.StaleSequence, signal);
                    return new ClipboardReadResult(null, null);
                }
            }
            finally
            {
                if (read is not null) _textReads.Release(read);
                if (operation is not null)
                    RetireTextRead(nativeRead ?? ObserveUnbridgedTextReadAsync(() => operation.Status, operation.GetResults),
                        operation.Cancel, operation.Close, nativeRead is null || !nativeRead.IsCompleted);
                else _textReadSlots.Release();
            }
            RecordDiagnostic(ClipboardDiagnosticDecision.TextReadCompleted, signal);
            if (string.IsNullOrWhiteSpace(text))
            {
                RecordDiagnostic(ClipboardDiagnosticDecision.EmptyText, signal);
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

            Task StartReadAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSignalStillCurrent(signal)) return Task.CompletedTask;
                RecordDiagnostic(ClipboardDiagnosticDecision.TextReadStarted, signal);
                operation = view.GetTextAsync();
                RecordDiagnostic(ClipboardDiagnosticDecision.TextOperationCreated, signal);
                // Retain the original task for eventual native completion/GetResults.
                nativeRead = operation.AsTask();
                read = _textReads.Register(signal.ClipboardSequenceNumber, GetClipboardSequenceNumber, QueueTextSequence);
                return Task.CompletedTask;
            }
        }

        var formatCount = view.AvailableFormats.Count;
        if (formatCount == 0 && retryEmptyView)
        {
            RecordDiagnostic(ClipboardDiagnosticDecision.EmptyViewRetry, signal, formatCount: 0);
            throw new EmptyClipboardViewException();
        }
        RecordDiagnostic(ClipboardDiagnosticDecision.UnsupportedFormat, signal, formatCount: formatCount);
        return new ClipboardReadResult(
            CreateRejectedSnapshot(signal, "unsupported-format"),
            null);
    }

    private async Task<ClipboardReadResult> ReadDeferredProviderAsync(CaptureSignal signal,
        Func<CancellationToken, Task<ClipboardReadResult>> read, CancellationToken cancellationToken)
    {
        // A malicious or broken provider cannot accumulate unlimited native reads. Do not
        // wait for a retired slot here: newer text must still reach the single capture worker.
        if (!await ClipboardProviderReadLifetime.TryReserveSlotAsync(_providerReadSlots, cancellationToken))
            return new ClipboardReadResult(CreateRejectedSnapshot(signal, "provider-read-capacity"), null);
        TextReadLease? lease = null;
        CancellationTokenSource? lifetime = null;
        Task<ClipboardReadResult>? operation = null;
        var consumed = false;
        try
        {
            lease = _providerReads.Register(signal.ClipboardSequenceNumber, GetClipboardSequenceNumber, QueueTextSequence);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.SupersededToken);
            lifetime.CancelAfter(TimeSpan.FromSeconds(5));
            operation = read(lifetime.Token);
            var result = await operation.WaitAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            consumed = true;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Supersession already admitted the replacement; a timeout simply skips the
            // unresponsive sequence. The retirement task retains streams/buffers until done.
            return new ClipboardReadResult(null, null);
        }
        finally
        {
            if (lease is not null) _providerReads.Release(lease);
            if (operation is null) { lifetime?.Dispose(); _providerReadSlots.Release(); }
            else
            {
                var id = Interlocked.Increment(ref _providerReadId);
                var retirement = RetireProviderReadAsync(operation, lifetime!, consumed);
                _retiredProviderReads[id] = retirement;
                _ = retirement.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    _retiredProviderReads.TryRemove(id, out _);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task RetireProviderReadAsync(Task<ClipboardReadResult> operation,
        CancellationTokenSource lifetime, bool consumed)
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            if (!consumed && result.ImageBytes is { } bytes) Array.Clear(bytes);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { _logger.LogDebug("Clipboard provider read retired ({Category}).", exception.GetType().Name); }
        finally { lifetime.Dispose(); _providerReadSlots.Release(); }
    }

    private async Task<T> ReadProviderOperationAsync<T>(Func<IAsyncOperation<T>> start, CancellationToken token)
    {
        IAsyncOperation<T>? operation = null;
        Task StartAsync()
        {
            token.ThrowIfCancellationRequested();
            operation = start();
            return Task.CompletedTask;
        }
        if (_dispatcher.HasThreadAccess) await StartAsync();
        else await _dispatcher.EnqueueAsync(StartAsync).ConfigureAwait(false);
        var ownedOperation = operation!;
        Task<T> nativeRead;
        try { nativeRead = ownedOperation.AsTask(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Bridge setup failure is not evidence that native ownership ended.
            nativeRead = ObserveUnbridgedProviderReadAsync(() => ownedOperation.Status, ownedOperation.GetResults);
        }
        return await ClipboardProviderReadLifetime.CompleteAsync(nativeRead, ownedOperation.Cancel,
            ownedOperation.Close, token, exception => _logger.LogDebug(
                "Clipboard provider teardown failed ({Category}).", exception.GetType().Name)).ConfigureAwait(false);
    }

    internal static Task<T> ObserveUnbridgedProviderReadAsync<T>(Func<AsyncStatus> readStatus, Func<T> getResults) => Task.Run(async () =>
    {
        while (true)
        {
            AsyncStatus? status = null;
            try { status = readStatus(); }
            catch (Exception exception) when (exception.HResult == unchecked((int)0x80000013))
            { return default(T)!; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            if (status is AsyncStatus.Completed or AsyncStatus.Error or AsyncStatus.Canceled) return getResults();
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    });

    private void RetireTextRead(Task<string> nativeRead, Action cancel, Action close, bool requestCancellation)
    {
        var id = Interlocked.Increment(ref _retiredTextReadId);
        var cleanup = ObserveTextReadCompletionAsync(nativeRead, cancel, close, requestCancellation,
            exception => _logger.LogDebug("Clipboard text operation cleanup failed ({Category}, {HResult}).",
                exception.GetType().Name, exception.HResult), () => _textReadSlots.Release());
        _retiredTextReads[id] = cleanup;
        _ = cleanup.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _retiredTextReads.TryRemove(id, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static Task ObserveTextReadCompletionAsync(Task<string> nativeRead, Action cancel, Action close,
        bool requestCancellation, Action<Exception>? onFailure = null, Action? onClosed = null) => Task.Run(async () =>
    {
        var cancellation = requestCancellation ? Task.Run(() => TryNativeAction(cancel)) : Task.CompletedTask;
        try
        {
            try { await nativeRead.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception); }
        }
        finally
        {
            try { await cancellation.ConfigureAwait(false); }
            finally
            {
                // Both the ordinary bridge and a setup-failure observer have
                // established terminal state before native Close and slot release.
                try
                {
                    while (true)
                    {
                        try { close(); break; }
                        catch (Exception exception) when (exception.HResult == unchecked((int)0x80000013)) { break; }
                        catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception); }
                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                }
                finally { onClosed?.Invoke(); }
            }
        }

        void TryNativeAction(Action action)
        {
            try { action(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { Report(exception); }
        }

        void Report(Exception exception)
        {
            try { onFailure?.Invoke(exception); }
            catch (Exception callbackFailure) when (callbackFailure is not OutOfMemoryException) { }
        }
    });

    internal static Task<string> ObserveUnbridgedTextReadAsync(Func<AsyncStatus> readStatus, Func<string> getResults) => Task.Run(async () =>
    {
        // AsTask can fail during bridge setup. A created operation still owns its
        // slot until terminal state is known; Cancel alone is not that evidence.
        while (true)
        {
            AsyncStatus? status = null;
            try { status = readStatus(); }
            catch (Exception exception) when (exception.HResult == unchecked((int)0x80000013))
            {
                return string.Empty; // RO_E_CLOSED proves native ownership ended.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            if (status is AsyncStatus.Completed or AsyncStatus.Error or AsyncStatus.Canceled)
                return getResults();
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    });

    internal sealed class TextReadCoordinator
    {
        private readonly object _gate = new();
        private TextReadLease? _active;

        internal bool HasActiveRead { get { lock (_gate) return _active is not null; } }

        internal TextReadLease Register(uint sequence, Func<uint> readCurrentSequence, Func<uint, bool> queue)
        {
            var read = new TextReadLease(sequence);
            lock (_gate) _active = read;
            // The notification may have arrived before this lease was published.
            // Read native state after publication rather than reuse its snapshot.
            try { Notify(readCurrentSequence(), null, queue); }
            catch { Release(read); throw; }
            return read;
        }

        internal void Notify(uint currentSequence, uint? admittedSequence, Func<uint, bool> queue)
        {
            TextReadLease? read;
            lock (_gate) read = _active;
            if (read is null || read.Sequence == 0 || currentSequence == 0 || read.Sequence == currentSequence) return;
            if (admittedSequence != currentSequence && !queue(currentSequence)) return;
            read.Supersede();
        }

        internal void Release(TextReadLease read)
        {
            lock (_gate) { if (ReferenceEquals(_active, read)) _active = null; }
            read.Dispose();
        }
    }

    internal sealed class TextReadLease(uint sequence) : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _superseded = new();
        private bool _disposed;
        private int _isSuperseded;
        internal uint Sequence { get; } = sequence;
        internal CancellationToken SupersededToken => _superseded.Token;
        internal bool IsSuperseded => Volatile.Read(ref _isSuperseded) != 0;

        internal void Supersede()
        {
            lock (_gate)
            {
                if (_disposed || IsSuperseded) return;
                Volatile.Write(ref _isSuperseded, 1);
                // This private token is used only by the managed WaitAsync waiter;
                // native cancellation is never a token callback on the listener.
                _superseded.Cancel();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _superseded.Dispose();
            }
        }
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
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
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
            checked((int)decoder.OrientedPixelWidth),
            checked((int)decoder.OrientedPixelHeight),
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

    private Task<IReadOnlyList<DropItem>> CommitSnapshotWithDiagnosticsAsync(
        ClipboardSnapshot snapshot, CaptureSignal signal, CancellationToken cancellationToken)
    {
        // Normal capture retains the original task/continuation path.
        if (!_diagnostics.IsEnabled) return CommitSnapshotAsync(snapshot, signal.ClipboardSequenceNumber, cancellationToken);
        return TraceCommitAsync();

        async Task<IReadOnlyList<DropItem>> TraceCommitAsync()
        {
            RecordDiagnostic(ClipboardDiagnosticDecision.RepositoryCommitStarted, signal);
            var committed = await CommitSnapshotAsync(snapshot, signal.ClipboardSequenceNumber, cancellationToken).ConfigureAwait(false);
            RecordDiagnostic(ClipboardDiagnosticDecision.RepositoryCommitCompleted, signal);
            return committed;
        }
    }

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

    private void PublishStatus(AppUiMessage? message, ClipboardRecordingState? state = null)
    {
        if (StatusChanged is not { } handlers) return;
        var status = CreateStatus(message, state);
        foreach (EventHandler<ClipboardCaptureStatus> handler in handlers.GetInvocationList())
        {
            try { handler(this, status); }
            catch (Exception exception) { _logger.LogWarning(exception, "Clipboard status subscriber failed ({Category}).", exception.GetType().Name); }
        }
    }

    private ClipboardCaptureStatus CreateStatus(AppUiMessage? message, ClipboardRecordingState? state = null) =>
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
            message?.Render(_strings),
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

    private sealed class EmptyClipboardViewException : Exception { }

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
