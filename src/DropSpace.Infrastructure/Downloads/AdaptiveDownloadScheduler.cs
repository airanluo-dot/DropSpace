// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
namespace DropSpace.Infrastructure.Downloads;

/// <summary>Grows a bounded worker group only while transfers make progress.</summary>
internal static class AdaptiveDownloadScheduler
{
    public static async Task RunAsync(int count, int maximum, Func<int, Task> download,
        Func<long> completedBytes, CancellationToken cancellationToken)
    {
        if (count <= 0) return;
        maximum = Math.Clamp(maximum, 1, Math.Min(count, DownloadConnectionBudget.Maximum));
        var next = -1;
        var workers = new List<Task>();
        void AddWorker() => workers.Add(WorkerAsync());
        async Task WorkerAsync()
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref next);
                if (index >= count) return;
                await download(index).ConfigureAwait(false);
            }
        }
        for (var i = 0; i < Math.Min(4, maximum); i++) AddWorker();
        var previous = completedBytes();
        try
        {
            while (workers.Count < maximum && Volatile.Read(ref next) < count - 1 && !workers.Any(task => task.IsFaulted))
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                var current = completedBytes();
                if (current > previous)
                {
                    var target = Math.Min(maximum, workers.Count * 2);
                    while (workers.Count < target && Volatile.Read(ref next) < count - 1) AddWorker();
                }
                previous = current;
            }
        }
        finally
        {
            // Observe every worker on cancellation or source failure as well as on success.
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }
}
