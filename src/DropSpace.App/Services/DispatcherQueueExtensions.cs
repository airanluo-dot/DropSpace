using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services;

public static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this DispatcherQueue dispatcher, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
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

    public static Task<T> EnqueueAsync<T>(this DispatcherQueue dispatcher, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
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
