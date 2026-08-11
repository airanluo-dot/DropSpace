using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
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
    DispatcherQueue dispatcher)
{
    public async Task<ClipboardIntegrationMetrics> RunAsync(CancellationToken cancellationToken = default)
    {
        var initial = capture.Status;
        if (!initial.ListenerRegistered)
        {
            throw new InvalidOperationException("The Win32 clipboard listener was not registered.");
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
            await SetClipboardTextAsync(first);
            await WaitForAsync(
                () => capture.Status.ObservedEvents > baseline.ObservedEvents,
                "WM_CLIPBOARDUPDATE for first test text",
                cancellationToken);
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
            await SetClipboardTextAsync(first);
            await WaitForAsync(
                () => capture.Status.ObservedEvents > beforeConsecutiveDuplicate.ObservedEvents,
                "WM_CLIPBOARDUPDATE for consecutive duplicate text",
                cancellationToken);
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
            await SetClipboardTextAsync(second);
            await WaitForAsync(
                () => capture.Status.CapturedItems > afterFirst.CapturedItems,
                "repository capture for second test text",
                cancellationToken);
            var secondPersisted = await ContainsTextAsync(second, cancellationToken);
            if (!secondPersisted)
            {
                throw new InvalidOperationException("The second clipboard text did not reach the repository.");
            }

            var beforeNonConsecutiveRepeat = capture.Status;
            await SetClipboardTextAsync(first);
            await WaitForAsync(
                () => capture.Status.CapturedItems > beforeNonConsecutiveRepeat.CapturedItems,
                "repository capture for non-consecutive repeated text",
                cancellationToken);
            var nonConsecutiveDuplicatePreserved = await CountTextAsync(first, cancellationToken) == 2;
            if (!nonConsecutiveDuplicatePreserved)
            {
                throw new InvalidOperationException("A non-consecutive clipboard text was incorrectly collapsed.");
            }

            Directory.CreateDirectory(fileTestRoot);
            Directory.CreateDirectory(folderPath);
            await File.WriteAllTextAsync(filePath, "clipboard file reference smoke", cancellationToken);
            await File.WriteAllBytesAsync(secondFilePath, [1, 2, 3, 4], cancellationToken);
            var beforeFile = capture.Status;
            await SetClipboardItemsAsync(filePath, secondFilePath, folderPath);
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

            await capture.PauseAsync(cancellationToken);
            var beforePausedWrite = capture.Status;
            await SetClipboardTextAsync(paused);
            await WaitForAsync(
                () => capture.Status.ObservedEvents > beforePausedWrite.ObservedEvents,
                "clipboard notification while paused",
                cancellationToken);
            await Task.Delay(300, cancellationToken);
            var pauseVerified = capture.Status.CapturedItems == beforePausedWrite.CapturedItems &&
                                !await ContainsTextAsync(paused, cancellationToken);
            if (!pauseVerified)
            {
                throw new InvalidOperationException("Clipboard pause did not block repository capture.");
            }

            await capture.ResumeAsync(cancellationToken);
            var beforeResumeWrite = capture.Status;
            await SetClipboardTextAsync(resumed);
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
            await capture.CopyTextAsync(selfWrite, cancellationToken);
            await WaitForAsync(
                () => capture.Status.ObservedEvents > beforeSelfWrite.ObservedEvents,
                "clipboard notification for a DropSpace self-write",
                cancellationToken);
            await Task.Delay(350, cancellationToken);
            var selfWriteVerified = capture.Status.CapturedItems == beforeSelfWrite.CapturedItems &&
                                    !await ContainsTextAsync(selfWrite, cancellationToken);
            if (!selfWriteVerified)
            {
                throw new InvalidOperationException("Clipboard self-write suppression failed.");
            }

            var final = capture.Status;
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

    private Task SetClipboardTextAsync(string text) => dispatcher.EnqueueAsync(() =>
    {
        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.CompletedTask;
    });

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
        Clipboard.SetContent(package);
        Clipboard.Flush();
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

    private static async Task WaitForAsync(
        Func<bool> predicate,
        string operation,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (!predicate())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {operation}.");
            }

            await Task.Delay(40, cancellationToken);
        }
    }
}
