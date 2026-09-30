using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Storage;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DropSpace.App.Services;

public sealed record ClipboardIntegrationMetrics(
    bool ListenerRegistered,
    long ObservedUpdateDelta,
    long SuccessfulCaptureDelta,
    long SuppressedConsecutiveDuplicateDelta,
    long FailedReadDelta,
    bool FirstTextPersisted,
    bool SecondTextPersisted,
    bool ConsecutiveDuplicateSuppressionVerified,
    bool NonConsecutiveDuplicatePreserved,
    bool FileReferencePersisted,
    bool PauseVerified,
    bool ResumeVerified,
    bool SelfWriteSuppressionVerified);

public sealed class ClipboardIntegrationSmoke(
    ClipboardCaptureService capture,
    IItemRepository repository,
    DispatcherQueue dispatcher,
    AppStoragePaths paths)
{
    private ClipboardDiagnosticSession? _diagnosticSession;

    public async Task<ClipboardIntegrationMetrics> RunAsync(CancellationToken cancellationToken = default, bool runControlledTextCases = false)
    {
        capture.BeginDiagnosticSession();
        await using var diagnostics = new ClipboardDiagnosticSession(paths.Logs, () => capture.DiagnosticSnapshot, capture.EndDiagnosticSession);
        _diagnosticSession = diagnostics;
        BeginStage(ClipboardSmokeStage.Initial);
        WriteDiagnostics();
        var initial = capture.Status;
        if (!initial.ListenerRegistered)
        {
            var exception = new InvalidOperationException("The Win32 clipboard listener was not registered.");
            capture.RecordSmokeDiagnostic(ClipboardDiagnosticDecision.SmokeFailed, exception);
            await diagnostics.FlushAsync();
            throw exception;
        }

        var wasPaused = initial.State == ClipboardRecordingState.Paused;
        var token = $"DropSpaceClipboardSmoke{Guid.NewGuid():N}";
        var first = $"{token}-first";
        var second = $"{token}-second";
        var paused = $"{token}-paused";
        var resumed = $"{token}-resumed";
        var selfWrite = $"{token}-self";
        var fileTestRoot = Path.Combine(Path.GetTempPath(), "DropSpace-clipboard-smoke", token);
        var filePath = Path.Combine(fileTestRoot, $"{token}-file.txt");
        var secondFilePath = Path.Combine(fileTestRoot, $"{token}-second.bin");
        var folderPath = Path.Combine(fileTestRoot, $"{token}-folder");
        try
        {
            if (wasPaused)
            {
                await capture.ResumeAsync(cancellationToken);
            }

            var baseline = capture.Status;
            BeginStage(ClipboardSmokeStage.FirstTextWrite);
            await SetClipboardTextAsync(first);
            BeginStage(ClipboardSmokeStage.FirstTextNotification);
            await WaitForAsync(
                () => capture.Status.ObservedEvents > baseline.ObservedEvents,
                "WM_CLIPBOARDUPDATE for first test text",
                cancellationToken);
            BeginStage(ClipboardSmokeStage.FirstTextCapture);
            await WaitForAsync(
                () => capture.Status.CapturedItems > baseline.CapturedItems,
                "repository capture for first test text",
                cancellationToken);
            var firstPersisted = await ContainsTextAsync(first, cancellationToken);
            if (!firstPersisted)
            {
                throw new InvalidOperationException("The first clipboard text did not reach the repository.");
            }

            var beforeConsecutiveDuplicate = capture.Status;
            BeginStage(ClipboardSmokeStage.ConsecutiveTextWrite);
            await SetClipboardTextAsync(first);
            BeginStage(ClipboardSmokeStage.ConsecutiveTextNotification);
            await WaitForAsync(
                () => capture.Status.ObservedEvents > beforeConsecutiveDuplicate.ObservedEvents,
                "WM_CLIPBOARDUPDATE for consecutive duplicate text",
                cancellationToken);
            BeginStage(ClipboardSmokeStage.ConsecutiveSuppression);
            await WaitForAsync(
                () => capture.Status.SuppressedConsecutiveDuplicates > beforeConsecutiveDuplicate.SuppressedConsecutiveDuplicates,
                "consecutive duplicate suppression",
                cancellationToken);
            var consecutiveDuplicateSuppressed =
                capture.Status.CapturedItems == beforeConsecutiveDuplicate.CapturedItems &&
                await CountTextAsync(first, cancellationToken) == 1;
            if (!consecutiveDuplicateSuppressed)
            {
                throw new InvalidOperationException("A consecutive duplicate clipboard text created another history item.");
            }

            var afterFirst = capture.Status;
            BeginStage(ClipboardSmokeStage.SecondTextWrite);
            await SetClipboardTextAsync(second);
            BeginStage(ClipboardSmokeStage.SecondTextCapture);
            await WaitForAsync(
                () => capture.Status.CapturedItems > afterFirst.CapturedItems,
                "repository capture for second test text",
                cancellationToken);
            var secondPersisted = await ContainsTextAsync(second, cancellationToken);
            if (!secondPersisted)
            {
                throw new InvalidOperationException("The second clipboard text did not reach the repository.");
            }
            if (capture.Status.ObservedEvents <= afterFirst.ObservedEvents)
                throw new InvalidOperationException("The second clipboard text did not produce an observed update.");

            var beforeNonConsecutiveRepeat = capture.Status;
            BeginStage(ClipboardSmokeStage.NonConsecutiveTextWrite);
            await SetClipboardTextAsync(first);
            BeginStage(ClipboardSmokeStage.NonConsecutiveTextCapture);
            await WaitForAsync(
                () => capture.Status.CapturedItems > beforeNonConsecutiveRepeat.CapturedItems,
                "repository capture for non-consecutive repeated text",
                cancellationToken);
            var nonConsecutiveDuplicatePreserved = await CountTextAsync(first, cancellationToken) == 2;
            if (!nonConsecutiveDuplicatePreserved)
            {
                throw new InvalidOperationException("A non-consecutive clipboard text was incorrectly collapsed.");
            }

            if (runControlledTextCases)
                await RunControlledTextCasesAsync(token, cancellationToken);

            Directory.CreateDirectory(fileTestRoot);
            Directory.CreateDirectory(folderPath);
            await File.WriteAllTextAsync(filePath, "clipboard file reference smoke", cancellationToken);
            await File.WriteAllBytesAsync(secondFilePath, [1, 2, 3, 4], cancellationToken);
            var beforeFile = capture.Status;
            BeginStage(ClipboardSmokeStage.FileWrite);
            await SetClipboardItemsAsync(filePath, secondFilePath, folderPath);
            BeginStage(ClipboardSmokeStage.FileCapture);
            await WaitForAsync(
                () => capture.Status.CapturedItems >= beforeFile.CapturedItems + 3,
                "repository capture for mixed clipboard file/folder references",
                cancellationToken);
            var filePersisted = await ContainsFileAsync(filePath, cancellationToken) &&
                                await ContainsFileAsync(secondFilePath, cancellationToken) &&
                                await ContainsFileAsync(folderPath, cancellationToken);
            if (!filePersisted)
            {
                throw new InvalidOperationException("The mixed clipboard file/folder references did not reach the repository.");
            }

            BeginStage(ClipboardSmokeStage.Pause);
            await capture.PauseAsync(cancellationToken);
            var beforePausedWrite = capture.Status;
            BeginStage(ClipboardSmokeStage.PausedTextWrite);
            await SetClipboardTextAsync(paused);
            BeginStage(ClipboardSmokeStage.PausedTextNotification);
            await WaitForAsync(
                () => capture.Status.ObservedEvents > beforePausedWrite.ObservedEvents,
                "clipboard notification while paused",
                cancellationToken);
            BeginStage(ClipboardSmokeStage.PauseVerification);
            await Task.Delay(300, cancellationToken);
            var pauseVerified = capture.Status.CapturedItems == beforePausedWrite.CapturedItems &&
                                !await ContainsTextAsync(paused, cancellationToken);
            if (!pauseVerified)
            {
                throw new InvalidOperationException("Clipboard pause did not block repository capture.");
            }

            BeginStage(ClipboardSmokeStage.Resume);
            await capture.ResumeAsync(cancellationToken);
            var beforeResumeWrite = capture.Status;
            BeginStage(ClipboardSmokeStage.ResumedTextWrite);
            await SetClipboardTextAsync(resumed);
            BeginStage(ClipboardSmokeStage.ResumedTextCapture);
            await WaitForAsync(
                () => capture.Status.CapturedItems > beforeResumeWrite.CapturedItems,
                "clipboard capture after resume",
                cancellationToken);
            var resumeVerified = await ContainsTextAsync(resumed, cancellationToken);
            if (!resumeVerified)
            {
                throw new InvalidOperationException("Clipboard resume did not restore repository capture.");
            }

            var beforeSelfWrite = capture.Status;
            BeginStage(ClipboardSmokeStage.SelfWrite);
            await capture.CopyTextAsync(selfWrite, cancellationToken);
            BeginStage(ClipboardSmokeStage.SelfWriteNotification);
            await WaitForAsync(
                () => capture.Status.ObservedEvents > beforeSelfWrite.ObservedEvents,
                "clipboard notification for a DropSpace self-write",
                cancellationToken);
            BeginStage(ClipboardSmokeStage.SelfWriteVerification);
            await Task.Delay(350, cancellationToken);
            var selfWriteVerified = capture.Status.CapturedItems == beforeSelfWrite.CapturedItems &&
                                    !await ContainsTextAsync(selfWrite, cancellationToken);
            if (!selfWriteVerified)
            {
                throw new InvalidOperationException("Clipboard self-write suppression failed.");
            }

            var final = capture.Status;
            BeginStage(ClipboardSmokeStage.Complete);
            capture.RecordSmokeDiagnostic(ClipboardDiagnosticDecision.SmokeCompleted);
            await diagnostics.FlushAsync();
            return new ClipboardIntegrationMetrics(
                final.ListenerRegistered,
                final.ObservedEvents - baseline.ObservedEvents,
                final.CapturedItems - baseline.CapturedItems,
                final.SuppressedConsecutiveDuplicates - baseline.SuppressedConsecutiveDuplicates,
                final.FailedReads - baseline.FailedReads,
                firstPersisted,
                secondPersisted,
                consecutiveDuplicateSuppressed,
                nonConsecutiveDuplicatePreserved,
                filePersisted,
                pauseVerified,
                resumeVerified,
                selfWriteVerified);
        }
        catch (Exception exception)
        {
            // Preserve failure state before smoke rows/clipboard are cleared in finally.
            capture.RecordSmokeDiagnostic(ClipboardDiagnosticDecision.SmokeFailed, exception);
            await diagnostics.FlushAsync();
            throw;
        }
        finally
        {
            await RemoveSmokeItemsAsync(token, CancellationToken.None);
            await dispatcher.EnqueueAsync(() =>
            {
                Clipboard.Clear();
                return Task.CompletedTask;
            });
            if (wasPaused && capture.Status.State != ClipboardRecordingState.Paused)
            {
                await capture.PauseAsync(CancellationToken.None);
            }
            else if (!wasPaused && capture.Status.State == ClipboardRecordingState.Paused)
            {
                await capture.ResumeAsync(CancellationToken.None);
            }

            if (Directory.Exists(fileTestRoot))
            {
                Directory.Delete(fileTestRoot, recursive: true);
            }
        }
    }

    private async Task RunControlledTextCasesAsync(string token, CancellationToken cancellationToken)
    {
        foreach (var profile in new[] { ClipboardSmokeProfile.Immediate, ClipboardSmokeProfile.DispatcherYield, ClipboardSmokeProfile.FixedDelay })
        {
            for (var cycle = 1; cycle <= 4; cycle++)
            {
                var first = $"{token}-controlled-{profile}-{cycle}-first";
                var second = $"{token}-controlled-{profile}-{cycle}-second";
                var baseline = capture.Status;
                BeginStage(ClipboardSmokeStage.FirstTextWrite, profile, cycle);
                await SetClipboardTextAsync(first);
                BeginStage(ClipboardSmokeStage.FirstTextCapture, profile, cycle);
                await WaitForAsync(() => capture.Status.CapturedItems > baseline.CapturedItems,
                    "controlled first-text capture", cancellationToken);
                if (capture.Status.ObservedEvents <= baseline.ObservedEvents || await CountTextAsync(first, cancellationToken) != 1)
                    throw new InvalidOperationException("Controlled first text was not persisted exactly once.");

                var beforeDuplicate = capture.Status;
                BeginStage(ClipboardSmokeStage.ConsecutiveTextWrite, profile, cycle);
                await SetClipboardTextAsync(first);
                BeginStage(ClipboardSmokeStage.ConsecutiveSuppression, profile, cycle);
                await WaitForAsync(() => capture.Status.SuppressedConsecutiveDuplicates > beforeDuplicate.SuppressedConsecutiveDuplicates,
                    "controlled consecutive suppression", cancellationToken);
                if (capture.Status.ObservedEvents <= beforeDuplicate.ObservedEvents ||
                    capture.Status.CapturedItems != beforeDuplicate.CapturedItems || await CountTextAsync(first, cancellationToken) != 1)
                    throw new InvalidOperationException("Controlled consecutive duplicate created another history item.");

                if (profile == ClipboardSmokeProfile.DispatcherYield)
                    await dispatcher.EnqueueAsync(() => Task.CompletedTask);
                else if (profile == ClipboardSmokeProfile.FixedDelay)
                    await Task.Delay(200, cancellationToken);

                var beforeSecond = capture.Status;
                BeginStage(ClipboardSmokeStage.SecondTextWrite, profile, cycle);
                await SetClipboardTextAsync(second);
                BeginStage(ClipboardSmokeStage.SecondTextCapture, profile, cycle);
                await WaitForAsync(() => capture.Status.CapturedItems > beforeSecond.CapturedItems,
                    "controlled second-text capture", cancellationToken);
                if (capture.Status.ObservedEvents <= beforeSecond.ObservedEvents || !await ContainsTextAsync(second, cancellationToken))
                    throw new InvalidOperationException("Controlled second text lacked notification or repository persistence.");

                var beforeRepeat = capture.Status;
                BeginStage(ClipboardSmokeStage.NonConsecutiveTextWrite, profile, cycle);
                await SetClipboardTextAsync(first);
                BeginStage(ClipboardSmokeStage.NonConsecutiveTextCapture, profile, cycle);
                await WaitForAsync(() => capture.Status.CapturedItems > beforeRepeat.CapturedItems,
                    "controlled non-consecutive capture", cancellationToken);
                if (capture.Status.ObservedEvents <= beforeRepeat.ObservedEvents || await CountTextAsync(first, cancellationToken) != 2)
                    throw new InvalidOperationException("Controlled non-consecutive text lacked notification or its second history row.");
                WriteDiagnostics();
            }
        }
    }

    private void BeginStage(ClipboardSmokeStage stage, ClipboardSmokeProfile profile = ClipboardSmokeProfile.Baseline, int cycle = 0)
    {
        capture.BeginDiagnosticStage(stage, profile, cycle);
    }

    private void WriteDiagnostics() => _diagnosticSession?.RequestFlush();

    private Task SetClipboardTextAsync(string text)
    {
        return dispatcher.EnqueueAsync(() =>
        {
            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.SetText(text);
            return ClipboardAccessPolicy.SetContentAsync(() =>
            {
                Clipboard.SetContent(package);
                Clipboard.Flush();
            });
        });
    }

    private Task SetClipboardItemsAsync(params string[] paths) => dispatcher.EnqueueAsync(async () =>
    {
        var items = new List<IStorageItem>(paths.Length);
        foreach (var path in paths)
        {
            items.Add(Directory.Exists(path)
                ? await StorageFolder.GetFolderFromPathAsync(path)
                : await StorageFile.GetFileFromPathAsync(path));
        }

        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetStorageItems(items, readOnly: true);
        await ClipboardAccessPolicy.SetContentAsync(() =>
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
        });
    });

    private async Task<bool> ContainsTextAsync(string text, CancellationToken cancellationToken)
    {
        var matches = await repository.QueryAsync(
            new ItemQuery(Source: ItemSource.Clipboard, Search: text, Limit: 10),
            cancellationToken);
        return matches.Any(item => string.Equals(item.Text?.InlineText, text, StringComparison.Ordinal));
    }

    private async Task<int> CountTextAsync(string text, CancellationToken cancellationToken)
    {
        var matches = await repository.QueryAsync(
            new ItemQuery(Source: ItemSource.Clipboard, Search: text, Limit: 100),
            cancellationToken);
        return matches.Count(item => string.Equals(item.Text?.InlineText, text, StringComparison.Ordinal));
    }

    private async Task<bool> ContainsFileAsync(string path, CancellationToken cancellationToken)
    {
        var matches = await repository.QueryAsync(
            new ItemQuery(Source: ItemSource.Clipboard, Search: Path.GetFileName(path), Limit: 10),
            cancellationToken);
        return matches.Any(item => string.Equals(
            item.File?.OriginalPath,
            path,
            StringComparison.OrdinalIgnoreCase));
    }

    private async Task RemoveSmokeItemsAsync(string token, CancellationToken cancellationToken)
    {
        var matches = await repository.QueryAsync(
            new ItemQuery(Source: ItemSource.Clipboard, Search: token, Limit: 100),
            cancellationToken);
        foreach (var item in matches)
        {
            await repository.RemoveAsync(item.Id, cancellationToken);
        }
    }

    private async Task WaitForAsync(
        Func<bool> predicate,
        string operation,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        capture.RecordSmokeDiagnostic(ClipboardDiagnosticDecision.WaitStarted);
        var previous = capture.DiagnosticCurrentState;
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {operation}.");
            }

            await Task.Delay(40, cancellationToken);
            var current = capture.DiagnosticCurrentState;
            if (current.Counters != previous.Counters || current.NativeSequence != previous.NativeSequence || current.WorkerStatus != previous.WorkerStatus)
                capture.RecordSmokeDiagnostic(ClipboardDiagnosticDecision.WaitProgress);
            previous = current;
        }
        capture.RecordSmokeDiagnostic(ClipboardDiagnosticDecision.WaitCompleted);
    }
}
