using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DropSpace.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ClipboardDeferredProviderNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task NewEagerTextIsCapturedWhileAnOlderTextProviderRemainsDeferred()
    {
        // This is a controlled native interleaving, not evidence that the original
        // eager SetText/Flush smoke used a deferred provider or failed this way.
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-deferred-clipboard", Guid.NewGuid().ToString("N")));
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? TestContext.TestRunDirectory ?? Path.GetTempPath();
        var diagnosticsRoot = Path.Combine(workspace, "artifacts", "diagnostics", $"native-deferred-read-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var repository = new SqliteItemRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
        var consumer = new NativeOwner(heartbeat: true);
        var producer = new NativeOwner(heartbeat: false);
        var notifications = new ClipboardNotificationService(NullLogger<ClipboardNotificationService>.Instance);
        var capture = new ClipboardCaptureService(repository, new JsonSettingsService(paths),
            new FilePayloadStore(paths), new FilePreviewCache(paths), new LocalFileReferenceService(),
            notifications, consumer.Queue, IdentityAppStringLocalizer.Instance, NullLogger<ClipboardCaptureService>.Instance);
        var unique = Guid.NewGuid().ToString("N");
        var first = $"DropSpaceDeferredNative-{unique}-first";
        var deferred = $"DropSpaceDeferredNative-{unique}-obsolete";
        var second = $"DropSpaceDeferredNative-{unique}-second";
        var provider = new HeldDataProvider(deferred);
        DataPackage? heldPackage = null;
        ClipboardDiagnosticSession? session = null;
        var checkpoint = Checkpoint.OwnersInitializing;
        uint? heldSequence = null;
        uint? heldPublishedSequence = null;
        long providerEventBaseline = 0;
        long heldReadStartIndex = 0;
        uint? secondSequence = null;
        long heartbeatBeforeSecond = 0;
        bool secondPublished = false;
        DateTimeOffset? secondDeadline = null;
        bool oldTextOperationCreated = false;
        Exception? originalFailure = null;
        var cleanupFailures = new List<object>();
        try
        {
            await consumer.InitializeAsync();
            await producer.InitializeAsync();
            await consumer.Queue.EnqueueAsync(() =>
            {
                notifications.Initialize(consumer.Window);
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.IsTrue(notifications.Status.IsRegistered, "The real native clipboard listener must register.");
            await repository.InitializeAsync();
            await capture.InitializeAsync();
            capture.BeginDiagnosticSession();
            session = new ClipboardDiagnosticSession(diagnosticsRoot, () => capture.DiagnosticSnapshot, capture.EndDiagnosticSession);

            checkpoint = Checkpoint.FirstText;
            capture.BeginDiagnosticStage(ClipboardSmokeStage.FirstTextWrite);
            var baseline = capture.Status;
            await PublishEagerAsync(producer, first);
            capture.BeginDiagnosticStage(ClipboardSmokeStage.FirstTextCapture);
            await WaitForAsync(() => capture.Status.CapturedItems > baseline.CapturedItems, "first-text capture");
            Assert.IsTrue(capture.Status.ObservedEvents > baseline.ObservedEvents, "First text must produce a real native notification.");
            Assert.AreEqual(1, await CountTextAsync(repository, first), "First text must be persisted exactly once.");

            checkpoint = Checkpoint.ConsecutiveSuppression;
            capture.BeginDiagnosticStage(ClipboardSmokeStage.ConsecutiveTextWrite);
            var beforeDuplicate = capture.Status;
            await PublishEagerAsync(producer, first);
            capture.BeginDiagnosticStage(ClipboardSmokeStage.ConsecutiveSuppression);
            await WaitForAsync(() => capture.Status.SuppressedConsecutiveDuplicates > beforeDuplicate.SuppressedConsecutiveDuplicates,
                "consecutive duplicate suppression");
            Assert.IsTrue(capture.Status.ObservedEvents > beforeDuplicate.ObservedEvents, "Duplicate text must produce a real native notification.");
            Assert.AreEqual(beforeDuplicate.CapturedItems, capture.Status.CapturedItems, "Consecutive suppression must not create a capture.");
            Assert.AreEqual(1, await CountTextAsync(repository, first), "Consecutive suppression must preserve one first-text row.");

            checkpoint = Checkpoint.ProviderPublishing;
            capture.BeginDiagnosticStage(ClipboardSmokeStage.SecondTextWrite);
            providerEventBaseline = capture.DiagnosticSnapshot.Events.LastOrDefault()?.Index ?? 0;
            await producer.SetContentAsync(() =>
            {
                heldPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                heldPackage.SetDataProvider(StandardDataFormats.Text, provider.OnRequested);
                // Flush would materialize the held provider before the test can
                // establish its actual in-flight GetTextAsync handshake.
                Clipboard.SetContent(heldPackage);
            }).WaitAsync(TimeSpan.FromSeconds(8));
            heldPublishedSequence = GetClipboardSequenceNumber();
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await WaitForAsync(() => capture.DiagnosticSnapshot.Events.Any(entry =>
                    entry.Index > providerEventBaseline && entry.Decision == ClipboardDiagnosticDecision.TextReadStarted),
                "real held-provider text read");
            var heldRead = capture.DiagnosticSnapshot.Events.Last(entry =>
                entry.Index > providerEventBaseline && entry.Decision == ClipboardDiagnosticDecision.TextReadStarted);
            heldSequence = heldRead.SignalSequence;
            heldReadStartIndex = heldRead.Index;
            Assert.IsFalse(capture.DiagnosticSnapshot.Events.Any(entry =>
                entry.Index > heldReadStartIndex && entry.SignalSequence == heldSequence &&
                entry.Decision is ClipboardDiagnosticDecision.TextReadCompleted or ClipboardDiagnosticDecision.ReadFailed or ClipboardDiagnosticDecision.CaptureFailed),
                "The controlled old read must still be pending before replacement publication.");
            Assert.IsFalse(provider.Released, "The provider must remain deferred during the replacement experiment.");
            Assert.IsTrue(provider.EarliestDeadline > DateTimeOffset.UtcNow.AddSeconds(10),
                "The platform provider deadline must leave room for the unchanged eight-second assertion.");

            checkpoint = Checkpoint.SecondTextPublishing;
            heartbeatBeforeSecond = consumer.Heartbeats;
            var beforeSecond = capture.Status;
            WriteCheckpoint();
            await PublishEagerAsync(producer, second);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            secondDeadline = deadline;
            secondPublished = true;
            secondSequence = GetClipboardSequenceNumber();
            checkpoint = Checkpoint.SecondTextCapture;
            capture.BeginDiagnosticStage(ClipboardSmokeStage.SecondTextCapture);
            Assert.IsTrue(provider.EarliestDeadline > deadline,
                "The measured provider deadline must cover the complete replacement capture budget.");
            // The exact original capture-count predicate and 8 s/40 ms budget
            // start after eager SetContent + Flush. Observation does not add a wait.
            await WaitForAsync(() => capture.Status.CapturedItems > beforeSecond.CapturedItems,
                "repository capture for second test text", deadline);
            Assert.IsTrue(capture.Status.ObservedEvents > beforeSecond.ObservedEvents,
                "Replacement text must produce an observed native update within the same capture budget.");
            Assert.AreEqual(1, await CountTextAsync(repository, second).WaitAsync(Remaining(deadline)),
                "Replacement text must reach the real repository exactly once within the same budget.");
            Assert.AreEqual(0, await CountTextAsync(repository, deferred).WaitAsync(Remaining(deadline)),
                "The obsolete deferred text must not be committed.");
            Assert.IsFalse(provider.Released, "Capture must not depend on completing the obsolete provider.");
            checkpoint = Checkpoint.Complete;
            capture.RecordSmokeDiagnostic(ClipboardDiagnosticDecision.SmokeCompleted);
            WriteCheckpoint();
            await session.FlushAsync();
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            capture.RecordSmokeDiagnostic(ClipboardDiagnosticDecision.SmokeFailed, exception);
            WriteCheckpoint();
            TestContext.WriteLine(JsonSerializer.Serialize(capture.DiagnosticSnapshot, JsonOptions));
            if (session is not null) await session.FlushAsync();
            else ClipboardDiagnosticWriter.TryWrite(diagnosticsRoot, capture.DiagnosticSnapshot);
            throw;
        }
        finally
        {
            // Return every native deferral before clearing global clipboard state
            // or retiring either message pump; cancelled obsolete requests are safe.
            await CleanupAsync(CleanupPhase.ProviderRelease, () => producer.Queue.EnqueueAsync(() =>
            {
                provider.ReleaseAll();
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(8)));
            await CleanupAsync(CleanupPhase.ClipboardClear, () => producer.Queue.EnqueueAsync(() =>
            {
                Clipboard.Clear();
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(8)));
            await CleanupAsync(CleanupPhase.CaptureDispose, () => capture.DisposeAsync().AsTask());
            await CleanupAsync(CleanupPhase.ListenerDispose, () => consumer.Queue.EnqueueAsync(() =>
            {
                notifications.Dispose();
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(8)));
            if (session is not null) await CleanupAsync(CleanupPhase.DiagnosticsDispose, () => session.DisposeAsync().AsTask());
            await CleanupAsync(CleanupPhase.ProducerDispose, () => producer.DisposeAsync().AsTask());
            await CleanupAsync(CleanupPhase.ConsumerDispose, () => consumer.DisposeAsync().AsTask());
            GC.KeepAlive(heldPackage);
            SqliteConnection.ClearAllPools();
            await CleanupAsync(CleanupPhase.StorageDelete, () =>
            {
                if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
                return Task.CompletedTask;
            });
            WriteCheckpoint(cleanup: true);
            if (originalFailure is null && cleanupFailures.Count > 0) Assert.Fail("Native fixture cleanup failed; see metadata.");
        }

        async Task CleanupAsync(CleanupPhase phase, Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                cleanupFailures.Add(new { phase, failure = ClipboardDiagnosticTrace.Classify(exception), hResult = exception.HResult });
            }
        }

        void WriteCheckpoint(bool cleanup = false)
        {
            oldTextOperationCreated = capture.DiagnosticSnapshot.Events.Any(entry =>
                entry.Index > heldReadStartIndex && entry.Decision == ClipboardDiagnosticDecision.TextOperationCreated && entry.SignalSequence == heldSequence);
            var metadata = new
            {
                schemaVersion = 1, processId = Environment.ProcessId, checkpoint,
                atUtc = DateTimeOffset.UtcNow, heldSequence, heldPublishedSequence, providerEventBaseline, heldReadStartIndex,
                secondSequence, secondPublished, secondDeadline, oldTextOperationCreated,
                consumerApartment = consumer.Apartment, consumerOleHResult = consumer.OleHResult,
                producerApartment = producer.Apartment, producerOleHResult = producer.OleHResult,
                consumerHeartbeats = consumer.Heartbeats, heartbeatBeforeSecond,
                providerRequests = provider.RequestCount, providerReleased = provider.Released,
                providerDeadline = provider.EarliestDeadline, providerCompletionFailures = provider.CompletionFailures,
                current = capture.DiagnosticCurrentState, cleanupFailures,
            };
            try
            {
                Directory.CreateDirectory(diagnosticsRoot);
                File.WriteAllText(Path.Combine(diagnosticsRoot, cleanup ? "native-deferred-read-cleanup.json" : "native-deferred-read-checkpoint.json"), JsonSerializer.Serialize(metadata, JsonOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            TestContext.WriteLine(JsonSerializer.Serialize(metadata, JsonOptions));
        }
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    [DataRow("StorageItems")]
    [DataRow("Bitmap")]
    public async Task NewTextIsCapturedWhileFileOrBitmapProviderRemainsDeferred(string format)
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-deferred-formats", Guid.NewGuid().ToString("N")));
        var repository = new SqliteItemRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
        var consumer = new NativeOwner(heartbeat: true);
        var producer = new NativeOwner(heartbeat: false);
        var notifications = new ClipboardNotificationService(NullLogger<ClipboardNotificationService>.Instance);
        var capture = new ClipboardCaptureService(repository, new JsonSettingsService(paths),
            new FilePayloadStore(paths), new FilePreviewCache(paths), new LocalFileReferenceService(),
            notifications, consumer.Queue, IdentityAppStringLocalizer.Instance, NullLogger<ClipboardCaptureService>.Instance);
        DataPackage? package = null;
        HeldDataProvider? provider = null;
        try
        {
            await consumer.InitializeAsync(); await producer.InitializeAsync();
            await consumer.Queue.EnqueueAsync(() => { notifications.Initialize(consumer.Window); return Task.CompletedTask; });
            await repository.InitializeAsync(); await capture.InitializeAsync();
            capture.BeginDiagnosticSession();
            var imagePath = Path.Combine(paths.Root, "deferred.png");
            await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
            // Dedicated DispatcherQueue callbacks have no SynchronizationContext.
            // Resolve the file before entering the OLE owner for synchronous publication.
            var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            await producer.SetContentAsync(() =>
            {
                object payload = format == "StorageItems" ? new IStorageItem[] { file } : RandomAccessStreamReference.CreateFromFile(file);
                provider = new HeldDataProvider(payload);
                package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetDataProvider(format == "StorageItems" ? StandardDataFormats.StorageItems : StandardDataFormats.Bitmap, provider.OnRequested);
                Clipboard.SetContent(package);
            }).WaitAsync(TimeSpan.FromSeconds(8));
            await provider!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var before = capture.Status.CapturedItems;
            var replacement = "DropSpaceDeferred-" + Guid.NewGuid().ToString("N");
            await PublishEagerAsync(producer, replacement);
            await WaitForAsync(() => capture.Status.CapturedItems > before, "replacement after deferred " + format);
            Assert.AreEqual(1, await CountTextAsync(repository, replacement));
            Assert.IsFalse(provider.Released, "The replacement must not depend on releasing the old provider.");
            Assert.AreEqual(0, (await repository.QueryAsync(new ItemQuery(Source: ItemSource.Clipboard, Limit: 100)))
                .Count(item => item.Kind is ItemKind.File or ItemKind.Image));
        }
        finally
        {
            try
            {
                await producer.Queue.EnqueueAsync(() => { provider?.ReleaseAll(); Clipboard.Clear(); return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(8));
            }
            finally
            {
                await capture.DisposeAsync();
                await consumer.Queue.EnqueueAsync(() => { notifications.Dispose(); return Task.CompletedTask; });
                await producer.DisposeAsync(); await consumer.DisposeAsync();
                GC.KeepAlive(package);
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task ClipboardWriteRetryRunsOnTheOleInitializedOwnerThread()
    {
        await using var owner = new NativeOwner(heartbeat: false);
        await owner.InitializeAsync();
        var attempts = 0;
        await owner.SetContentAsync(() =>
        {
            // Exercise the real retry delay without modifying the global clipboard.
            if (++attempts == 1) throw new COMException("Controlled clipboard busy", unchecked((int)0x800401D0));
        });
        Assert.AreEqual(2, attempts, "Both attempts must execute on the initialized owner thread.");
    }

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public Task TwoEmptyViewsRecoverMixedFilesWithoutAnotherClipboardWrite() =>
        VerifyEmptyClipboardViewAsync(EmptyViewScenario.RecoverMixedFiles);

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public Task ATrulyEmptyClipboardIsRejectedAfterExactlyTwoRetries() =>
        VerifyEmptyClipboardViewAsync(EmptyViewScenario.RemainEmpty);

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public Task ANonemptyUnsupportedClipboardViewIsNotRetried() =>
        VerifyEmptyClipboardViewAsync(EmptyViewScenario.UnsupportedNonempty);

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public Task ANewSequenceDuringAnEmptyViewProbeNeverCapturesTheOldFiles() =>
        VerifyEmptyClipboardViewAsync(EmptyViewScenario.Superseded);

    private enum EmptyViewScenario { RecoverMixedFiles, RemainEmpty, UnsupportedNonempty, Superseded }

    private async Task VerifyEmptyClipboardViewAsync(EmptyViewScenario scenario)
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-empty-view", Guid.NewGuid().ToString("N")));
        var repository = new SqliteItemRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
        var consumer = new NativeOwner(heartbeat: true);
        var producer = new NativeOwner(heartbeat: false);
        var notifications = new ClipboardNotificationService(NullLogger<ClipboardNotificationService>.Instance);
        var targetSequence = 0u;
        var targetReadAttempts = 0;
        var replacement = "DropSpaceEmptyViewReplacement-" + Guid.NewGuid().ToString("N");
        var baselineText = "DropSpaceEmptyViewBaseline-" + Guid.NewGuid().ToString("N");
        // Packages backing synthetic views remain strongly owned until teardown.
        var syntheticPackages = new List<DataPackage>();
        DataPackage? publishedPackage = null;
        var cleanupFailures = new List<Exception>();
        Exception? originalFailure = null;
        var capture = new ClipboardCaptureService(repository, new JsonSettingsService(paths),
            new FilePayloadStore(paths), new FilePreviewCache(paths), new LocalFileReferenceService(),
            notifications, consumer.Queue, IdentityAppStringLocalizer.Instance, NullLogger<ClipboardCaptureService>.Instance)
        {
            ClipboardViewReader = () =>
            {
                Assert.IsTrue(consumer.Queue.HasThreadAccess, "Every probe must run on the real OLE consumer owner.");
                var sequence = GetClipboardSequenceNumber();
                if (sequence != Volatile.Read(ref targetSequence) || sequence == 0)
                    return Clipboard.GetContent();

                var attempt = Interlocked.Increment(ref targetReadAttempts);
                if (scenario == EmptyViewScenario.RecoverMixedFiles && attempt <= 2)
                    return EmptyView();
                if (scenario == EmptyViewScenario.Superseded && attempt == 1)
                {
                    // The producer is an independent OLE owner. Complete publication
                    // before returning this empty view, guaranteeing that the pending
                    // old signal sees sequence advancement before its next probe.
                    PublishEagerAsync(producer, replacement).GetAwaiter().GetResult();
                    return EmptyView();
                }

                return Clipboard.GetContent();
            },
        };

        try
        {
            await consumer.InitializeAsync();
            await producer.InitializeAsync();
            await consumer.Queue.EnqueueAsync(() => { notifications.Initialize(consumer.Window); return Task.CompletedTask; })
                .WaitAsync(TimeSpan.FromSeconds(8));
            await repository.InitializeAsync();
            await capture.InitializeAsync();
            capture.BeginDiagnosticSession();
            await PublishEagerAsync(producer, baselineText);
            await WaitForAsync(() => capture.Status.CapturedItems > 0 &&
                    capture.DiagnosticCurrentState.LastProcessedSequence == GetClipboardSequenceNumber(),
                "baseline capture and sequence consumption");
            Assert.AreEqual(1, await CountTextAsync(repository, baselineText));

            var fileRoot = Path.Combine(paths.Root, "fixture");
            Directory.CreateDirectory(fileRoot);
            var filePath = Path.Combine(fileRoot, "first.txt");
            var secondPath = Path.Combine(fileRoot, "second.bin");
            var folderPath = Path.Combine(fileRoot, "folder");
            await File.WriteAllTextAsync(filePath, "fixture");
            await File.WriteAllBytesAsync(secondPath, [1, 2, 3, 4]);
            Directory.CreateDirectory(folderPath);
            // Dedicated queues have no managed synchronization context. Resolve all
            // StorageItems before entering the synchronous publication callback.
            IStorageItem[] items =
            [
                await StorageFile.GetFileFromPathAsync(filePath).AsTask().WaitAsync(TimeSpan.FromSeconds(8)),
                await StorageFile.GetFileFromPathAsync(secondPath).AsTask().WaitAsync(TimeSpan.FromSeconds(8)),
                await StorageFolder.GetFolderFromPathAsync(folderPath).AsTask().WaitAsync(TimeSpan.FromSeconds(8)),
            ];
            var before = capture.Status;
            capture.BeginDiagnosticStage(ClipboardSmokeStage.FileWrite);
            await PublishWithConsumerBarrierAsync(() =>
            {
                if (scenario == EmptyViewScenario.RemainEmpty)
                {
                    Clipboard.Clear();
                }
                else
                {
                    publishedPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                    if (scenario == EmptyViewScenario.UnsupportedNonempty)
                        publishedPackage.SetData("DropSpace.Test.Unsupported", "fixture");
                    else
                        publishedPackage.SetStorageItems(items, readOnly: true);
                    Clipboard.SetContent(publishedPackage);
                    Clipboard.Flush();
                }
                Volatile.Write(ref targetSequence, GetClipboardSequenceNumber());
            });
            Assert.AreNotEqual(0u, targetSequence, "The native publication must have a real sequence.");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            capture.BeginDiagnosticStage(ClipboardSmokeStage.FileCapture);

            if (scenario == EmptyViewScenario.RecoverMixedFiles)
            {
                await WaitForAsync(() => capture.Status.CapturedItems >= before.CapturedItems + 3,
                    "mixed files after two empty views", deadline);
                Assert.AreEqual(before.CapturedItems + 3, capture.Status.CapturedItems);
                Assert.AreEqual(targetSequence, GetClipboardSequenceNumber(), "Recovery must not depend on another clipboard publication.");
                var rows = await repository.QueryAsync(new ItemQuery(Source: ItemSource.Clipboard, Limit: 100));
                foreach (var path in new[] { filePath, secondPath, folderPath })
                    Assert.AreEqual(1, rows.Count(item => string.Equals(item.File?.OriginalPath, path, StringComparison.OrdinalIgnoreCase)),
                        "Each original file/folder must reach the real repository exactly once.");
                // The first two views are controlled; the real third native read
                // may encounter a legitimate COM retry without invalidating recovery.
                Assert.IsTrue(Volatile.Read(ref targetReadAttempts) >= 3);
                AssertTargetDecision(ClipboardDiagnosticDecision.EmptyViewRetry, 2);
                Assert.IsTrue(capture.DiagnosticSnapshot.Events.Count(entry =>
                    entry.SignalSequence == targetSequence && entry.Decision == ClipboardDiagnosticDecision.StorageItemsRead) >= 1);
                AssertTargetDecision(ClipboardDiagnosticDecision.UnsupportedFormat, 0);
                Assert.IsFalse(capture.DiagnosticSnapshot.Events.Any(entry =>
                    entry.SignalSequence == targetSequence && entry.Decision == ClipboardDiagnosticDecision.ReadFailed &&
                    entry.ReadAttempt is 1 or 2), "The two controlled empty views must never be counted as COM failures.");
            }
            else if (scenario == EmptyViewScenario.Superseded)
            {
                await WaitForAsync(() => capture.Status.CapturedItems > before.CapturedItems,
                    "replacement after empty-view supersession", deadline);
                Assert.AreEqual(before.CapturedItems + 1, capture.Status.CapturedItems);
                Assert.AreEqual(1, await CountTextAsync(repository, replacement));
                var rows = await repository.QueryAsync(new ItemQuery(Source: ItemSource.Clipboard, Limit: 100));
                var oldPaths = new[] { filePath, secondPath, folderPath };
                Assert.AreEqual(0, rows.Count(item => item.File is { OriginalPath: { } path } && oldPaths.Contains(path, StringComparer.OrdinalIgnoreCase)));
                Assert.AreEqual(1, Volatile.Read(ref targetReadAttempts), "The old sequence must not be read after advancement.");
                AssertTargetDecision(ClipboardDiagnosticDecision.EmptyViewRetry, 1);
                AssertTargetDecision(ClipboardDiagnosticDecision.RetrySequenceAdvanced, 1);
                AssertTargetDecision(ClipboardDiagnosticDecision.StorageItemsRead, 0);
                AssertTargetDecision(ClipboardDiagnosticDecision.RepositoryCommitStarted, 0);
            }
            else
            {
                await WaitForAsync(() => capture.DiagnosticSnapshot.Events.Any(entry =>
                        entry.SignalSequence == targetSequence && entry.Decision == ClipboardDiagnosticDecision.NoItemsCommitted),
                    "terminal rejected view", deadline);
                Assert.AreEqual(before.CapturedItems, capture.Status.CapturedItems);
                Assert.AreEqual(scenario == EmptyViewScenario.RemainEmpty ? 3 : 1, Volatile.Read(ref targetReadAttempts));
                AssertTargetDecision(ClipboardDiagnosticDecision.EmptyViewRetry, scenario == EmptyViewScenario.RemainEmpty ? 2 : 0);
                AssertTargetDecision(ClipboardDiagnosticDecision.UnsupportedFormat, 1);
                var unsupported = capture.DiagnosticSnapshot.Events.Single(entry =>
                    entry.SignalSequence == targetSequence && entry.Decision == ClipboardDiagnosticDecision.UnsupportedFormat);
                if (scenario == EmptyViewScenario.RemainEmpty)
                    Assert.AreEqual(0, unsupported.FormatCount);
                else
                    Assert.IsTrue(unsupported.FormatCount > 0, "A genuinely advertised unsupported format must not be treated as empty.");
                Assert.AreEqual(targetSequence, capture.DiagnosticCurrentState.LastProcessedSequence);
                // A terminal rejected view still breaks the consecutive-text run.
                // Re-copying the baseline must therefore create its second row.
                await PublishEagerAsync(producer, baselineText);
                await WaitForAsync(() => capture.Status.CapturedItems > before.CapturedItems,
                    "same text after a rejected view");
                Assert.AreEqual(2, await CountTextAsync(repository, baselineText));
            }

            if (scenario != EmptyViewScenario.RecoverMixedFiles)
                Assert.AreEqual(before.FailedReads, capture.Status.FailedReads, "An empty view is not a failed COM read.");
            Assert.AreEqual(ClipboardRecordingState.Recording, capture.Status.State);
            Assert.IsFalse(capture.DiagnosticSnapshot.Events.Any(entry => entry.SignalSequence == targetSequence &&
                entry.Decision == ClipboardDiagnosticDecision.CaptureFailed));
            if (scenario != EmptyViewScenario.RecoverMixedFiles)
                Assert.IsFalse(capture.DiagnosticSnapshot.Events.Any(entry => entry.SignalSequence == targetSequence &&
                    entry.Decision == ClipboardDiagnosticDecision.ReadFailed));
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            TestContext.WriteLine(JsonSerializer.Serialize(capture.DiagnosticSnapshot, JsonOptions));
            throw;
        }
        finally
        {
            // Stop capture first so cleanup Clear cannot start another controlled probe.
            await CleanupAsync(() => capture.DisposeAsync().AsTask());
            await CleanupAsync(() => consumer.Queue.EnqueueAsync(() => { notifications.Dispose(); return Task.CompletedTask; })
                .WaitAsync(TimeSpan.FromSeconds(8)));
            await CleanupAsync(() => producer.Queue.EnqueueAsync(() => { Clipboard.Clear(); return Task.CompletedTask; })
                .WaitAsync(TimeSpan.FromSeconds(8)));
            await CleanupAsync(() => producer.DisposeAsync().AsTask());
            await CleanupAsync(() => consumer.DisposeAsync().AsTask());
            GC.KeepAlive(publishedPackage);
            GC.KeepAlive(syntheticPackages);
            SqliteConnection.ClearAllPools();
            await CleanupAsync(() => { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true); return Task.CompletedTask; });
            foreach (var failure in cleanupFailures)
                TestContext.WriteLine($"Empty-view fixture cleanup failed: {failure.GetType().Name} (HRESULT {failure.HResult}).");
            if (originalFailure is null && cleanupFailures.Count > 0)
                Assert.Fail("Empty-view fixture cleanup failed; see test output.");
        }

        DataPackageView EmptyView()
        {
            var package = new DataPackage();
            syntheticPackages.Add(package);
            return package.GetView();
        }

        void AssertTargetDecision(ClipboardDiagnosticDecision decision, int count) =>
            Assert.AreEqual(count, capture.DiagnosticSnapshot.Events.Count(entry =>
                entry.SignalSequence == targetSequence && entry.Decision == decision), decision.ToString());

        async Task CleanupAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { cleanupFailures.Add(exception); }
        }

        async Task PublishWithConsumerBarrierAsync(Action publish)
        {
            // Pause the independent consumer only while the producer publishes and
            // records its final post-Flush sequence. Release before the eight-second
            // capture budget. This prevents arming against an intermediate sequence.
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = consumer.Queue.EnqueueAsync(() =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(8)))
                    throw new TimeoutException("The producer did not release the controlled consumer barrier.");
                return Task.CompletedTask;
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await producer.SetContentAsync(publish).WaitAsync(TimeSpan.FromSeconds(8));
            }
            finally
            {
                release.Set();
                await held.WaitAsync(TimeSpan.FromSeconds(8));
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static Task PublishEagerAsync(NativeOwner owner, string text) => owner.SetContentAsync(() =>
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }).WaitAsync(TimeSpan.FromSeconds(8));

    private static async Task<int> CountTextAsync(IItemRepository repository, string text) =>
        (await repository.QueryAsync(new ItemQuery(Source: ItemSource.Clipboard, Search: text, Limit: 100)))
        .Count(item => string.Equals(item.Text?.InlineText, text, StringComparison.Ordinal));

    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        var now = DateTimeOffset.UtcNow;
        return deadline > now ? deadline - now : TimeSpan.Zero;
    }

    private static async Task WaitForAsync(Func<bool> predicate, string operation, DateTimeOffset? deadline = null)
    {
        var expires = deadline ?? DateTimeOffset.UtcNow.AddSeconds(8);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= expires) throw new TimeoutException($"Timed out waiting for {operation}.");
            await Task.Delay(40).ConfigureAwait(false);
        }
    }

    private enum Checkpoint { OwnersInitializing, FirstText, ConsecutiveSuppression, ProviderPublishing, SecondTextPublishing, SecondTextCapture, Complete }
    private enum CleanupPhase { ProviderRelease, ClipboardClear, CaptureDispose, ListenerDispose, DiagnosticsDispose, ProducerDispose, ConsumerDispose, StorageDelete }

    private sealed class HeldDataProvider(object value)
    {
        private readonly object _gate = new();
        private readonly List<PendingRequest> _requests = [];
        private bool _released;
        private int _completionFailures;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released { get { lock (_gate) return _released; } }
        public int RequestCount { get { lock (_gate) return _requests.Count; } }
        public int CompletionFailures => Volatile.Read(ref _completionFailures);
        public DateTimeOffset? EarliestDeadline { get { lock (_gate) return _requests.Count == 0 ? null : _requests.Min(entry => entry.Deadline); } }

        public void OnRequested(DataProviderRequest request)
        {
            try
            {
                var deadline = request.Deadline;
                var deferral = request.GetDeferral();
                var pending = new PendingRequest(request, deferral, deadline);
                bool complete;
                lock (_gate)
                {
                    _requests.Add(pending);
                    complete = _released;
                }
                Entered.TrySetResult();
                if (complete) Complete(pending);
            }
            catch (Exception exception) { Entered.TrySetException(exception); }
        }

        public void ReleaseAll()
        {
            PendingRequest[] pending;
            lock (_gate) { _released = true; pending = _requests.ToArray(); }
            foreach (var entry in pending) Complete(entry);
        }

        private void Complete(PendingRequest pending)
        {
            if (Interlocked.Exchange(ref pending.Completed, 1) != 0) return;
            try { pending.Request.SetData(value); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { Interlocked.Increment(ref _completionFailures); }
            finally
            {
                try { pending.Deferral.Complete(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { Interlocked.Increment(ref _completionFailures); }
            }
        }

        private sealed class PendingRequest(DataProviderRequest request, DataProviderDeferral deferral, DateTimeOffset deadline)
        {
            public DataProviderRequest Request { get; } = request;
            public DataProviderDeferral Deferral { get; } = deferral;
            public DateTimeOffset Deadline { get; } = deadline;
            public int Completed;
        }
    }

    private sealed class NativeOwner(bool heartbeat) : IAsyncDisposable
    {
        private readonly DispatcherQueueController _controller = DispatcherQueueController.CreateOnDedicatedThread();
        private DispatcherQueueTimer? _timer;
        private long _heartbeats;
        private bool _oleInitialized;
        private int _ownerThreadId;
        public DispatcherQueue Queue => _controller.DispatcherQueue;
        public nint Window { get; private set; }
        public ApartmentState Apartment { get; private set; } = ApartmentState.Unknown;
        public int? OleHResult { get; private set; }
        public long Heartbeats => Interlocked.Read(ref _heartbeats);

        public Task InitializeAsync() => Queue.EnqueueAsync(() =>
        {
            _ownerThreadId = Environment.CurrentManagedThreadId;
            Apartment = Thread.CurrentThread.GetApartmentState();
            OleHResult = OleInitialize(0);
            Marshal.ThrowExceptionForHR(OleHResult.Value);
            _oleInitialized = true;
            Assert.AreEqual(ApartmentState.STA, Apartment, "Native clipboard owners must use a real STA.");
            Window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
            if (Window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (heartbeat)
            {
                _timer = Queue.CreateTimer();
                _timer.Interval = TimeSpan.FromMilliseconds(40);
                _timer.Tick += (_, _) => Interlocked.Increment(ref _heartbeats);
                _timer.Start();
            }
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(8));

        public async Task SetContentAsync(Action setContent)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var cancellationToken = timeout.Token;
            try
            {
                await ClipboardAccessPolicy.SetContentAsync(() =>
                {
                    // Retry delays have no owner SynchronizationContext. Dispatch every
                    // synchronous write back, without blocking the owner on its own queue.
                    if (Queue.HasThreadAccess) Write();
                    else Queue.EnqueueAsync(() => { Write(); return Task.CompletedTask; })
                        .WaitAsync(cancellationToken).GetAwaiter().GetResult();
                }, cancellationToken).WaitAsync(cancellationToken);
            }
            finally { timeout.Cancel(); }

            void Write()
            {
                // A timed-out queued callback may still be dequeued during cleanup.
                // It must not publish after this operation has returned.
                cancellationToken.ThrowIfCancellationRequested();
                Assert.IsTrue(Queue.HasThreadAccess, "Clipboard publication must run on its owner queue.");
                Assert.AreEqual(_ownerThreadId, Environment.CurrentManagedThreadId, "Clipboard publication must remain on the OLE-initialized thread.");
                Assert.AreEqual(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.IsTrue(_oleInitialized, "Clipboard publication requires a live OLE initialization.");
                setContent();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Queue.EnqueueAsync(() =>
                {
                    try
                    {
                        _timer?.Stop();
                        if (Window != 0)
                        {
                            if (!DestroyWindow(Window)) throw new Win32Exception(Marshal.GetLastWin32Error());
                            Window = 0;
                        }
                    }
                    finally
                    {
                        if (_oleInitialized) { OleUninitialize(); _oleInitialized = false; }
                    }
                    return Task.CompletedTask;
                }).WaitAsync(TimeSpan.FromSeconds(8));
            }
            finally { await _controller.ShutdownQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)); }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
