using DropSpace.App.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

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
    private readonly ILogger<ShareTargetActivationService> _logger;

    public ShareTargetActivationService(
        MainViewModel mainViewModel,
        DispatcherQueue dispatcher,
        ILogger<ShareTargetActivationService> logger)
    {
        _mainViewModel = mainViewModel;
        _dispatcher = dispatcher;
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
            if (!operation.Data.Contains(StandardDataFormats.StorageItems))
            {
                operation.ReportError("DropSpace 只接受 Windows 分享中的文件和文件夹。");
                return 0;
            }

            var storageItems = await operation.Data.GetStorageItemsAsync();
            var paths = storageItems
                .Take(MaximumSharedItems)
                .Where(static item => item is IStorageFile or IStorageFolder)
                .Select(static item => item.Path)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                operation.ReportError("分享内容中没有 DropSpace 可以访问的文件或文件夹。");
                return 0;
            }

            var accepted = await OnDispatcherAsync(
                () => _mainViewModel.AddPathsAsync(paths, cancellationToken));
            if (accepted == 0)
            {
                operation.ReportError("文件或文件夹未能加入 Temporary Space。");
                return 0;
            }

            operation.ReportCompleted();
            _logger.LogInformation(
                "Windows Share Target completed: StorageItems offered {OfferedCount}, validated {ValidatedCount}, accepted {AcceptedCount}. Paths were intentionally omitted.",
                storageItems.Count,
                paths.Length,
                accepted);
            return accepted;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Windows Share Target activation failed before completion.");
            try
            {
                operation.ReportError("DropSpace 无法处理本次分享。请重试或使用顶部拖放入口。");
            }
            catch (Exception reportException)
            {
                _logger.LogWarning(reportException, "Windows Share operation could not report its failure.");
            }

            return 0;
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
