using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using DropSpace.Infrastructure.Storage;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DropSpace.App.Services;

/// <summary>
/// Converts a Windows Share contract into the same AddPathsAsync mutation used by Main Window and
/// Overlay drops. It never writes Clipboard History and never moves or deletes the source items.
/// </summary>
public sealed class ShareTargetActivationService
{
    private const int MaximumSharedItems = 1_000;
    private readonly MainViewModel _mainViewModel;
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<ShareTargetActivationService> _logger;
    private readonly AppStoragePaths _paths;

    public ShareTargetActivationService(
        MainViewModel mainViewModel,
        DispatcherQueue dispatcher,
        IAppStringLocalizer strings,
        AppStoragePaths paths,
        ILogger<ShareTargetActivationService> logger)
    {
        _mainViewModel = mainViewModel;
        _dispatcher = dispatcher;
        _strings = strings;
        _paths = paths;
        _logger = logger;
    }

    public bool CanHandle(AppActivationArguments activation) =>
        activation.Kind == ExtendedActivationKind.ShareTarget &&
        activation.Data is ShareTargetActivatedEventArgs;

    public async Task<int> HandleAsync(
        AppActivationArguments activation,
        CancellationToken cancellationToken = default)
    {
        if (activation.Data is not ShareTargetActivatedEventArgs shareArgs)
        {
            throw new ArgumentException("The activation does not contain a Windows Share operation.", nameof(activation));
        }

        var operation = shareArgs.ShareOperation;
        try
        {
            operation.ReportStarted();
            if (operation.Data.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await operation.Data.GetStorageItemsAsync();
                var paths = storageItems
                    .Take(MaximumSharedItems)
                    .Where(static item => item is IStorageFile or IStorageFolder)
                    .Select(static item => item.Path)
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var acceptedPaths = paths.Length == 0
                    ? 0
                    : await OnDispatcherAsync(() => _mainViewModel.AddPathsBatchAsync(
                        paths,
                        null,
                        "windows-share-storage",
                        cancellationToken));
                if (acceptedPaths > 0)
                {
                    operation.ReportCompleted();
                    return acceptedPaths;
                }
            }

            string? sharedText = null;
            if (operation.Data.Contains(StandardDataFormats.WebLink))
            {
                sharedText = (await operation.Data.GetWebLinkAsync()).AbsoluteUri;
            }
            else if (operation.Data.Contains(StandardDataFormats.Text))
            {
                sharedText = await operation.Data.GetTextAsync();
            }

            if (!string.IsNullOrWhiteSpace(sharedText))
            {
                await OnDispatcherAsync(async () =>
                {
                    await _mainViewModel.AddTextToSpaceAsync(
                        sharedText,
                        "windows-share-text",
                        cancellationToken: cancellationToken);
                    return 1;
                });
                operation.ReportCompleted();
                return 1;
            }

            var accepted = operation.Data.Contains(StandardDataFormats.Bitmap)
                ? await MaterializeSharedBitmapAsync(operation.Data, cancellationToken)
                : 0;
            if (accepted == 0)
            {
                operation.ReportError(_strings.Get("ShareNoAccessibleItems"));
                return 0;
            }

            operation.ReportCompleted();
            _logger.LogInformation(
                "Windows Share Target completed: StorageItems offered {OfferedCount}, validated {ValidatedCount}, accepted {AcceptedCount}. Paths were intentionally omitted.",
                accepted,
                accepted,
                accepted);
            return accepted;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Windows Share Target activation failed before completion.");
            try
            {
                operation.ReportError(_strings.Get("ShareProcessingFailed"));
            }
            catch (Exception reportException)
            {
                _logger.LogWarning(reportException, "Windows Share operation could not report its failure.");
            }

            return 0;
        }
    }

    private async Task<int> MaterializeSharedBitmapAsync(
        DataPackageView data,
        CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var reference = await data.GetBitmapAsync();
        await using var input = (await reference.OpenReadAsync()).AsStreamForRead();
        if (input.CanSeek && input.Length > _mainViewModel.Settings.MaxImageBytes)
        {
            return 0;
        }

        var path = Path.Combine(_paths.Staging, $"shared-image-{Guid.NewGuid():N}.png");
        try
        {
            await using (var output = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81_920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81_920];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    total = checked(total + read);
                    if (total > _mainViewModel.Settings.MaxImageBytes)
                    {
                        throw new InvalidDataException("The shared bitmap exceeded the configured image byte limit.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var file = await StorageFile.GetFileFromPathAsync(path);
            using (var encoded = await file.OpenReadAsync())
            {
                await ImageDecoderPreflight.ValidateAsync(encoded, _mainViewModel.Settings.MaxImageBytes,
                    _mainViewModel.Settings.MaxImagePixels, cancellationToken);
            }
            return await OnDispatcherAsync(() => _mainViewModel.AddOwnedPathsBatchAsync(
                [path],
                null,
                "windows-share-image",
                _mainViewModel.Settings.MaxImageBytes,
                cancellationToken));
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception cleanup)
            {
                _logger.LogWarning("Share Target cleanup failed: {Category}.", cleanup.GetType().Name);
            }
            throw;
        }
    }

    private Task<T> OnDispatcherAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;
    }
}
