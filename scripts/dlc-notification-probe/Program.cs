using DropSpace.App.Services.Dlc;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

var scenario = args.Length == 0 ? "all" : args[0];
if (scenario is "all" or "transaction") await TransactionAsync();
if (scenario is "all" or "progress") await ProgressAsync();
if (scenario is "all" or "cancel") await CancellationAsync();
if (scenario is not ("all" or "transaction" or "progress" or "cancel"))
    throw new ArgumentException("Expected all, transaction, progress or cancel.");

static async Task TransactionAsync()
{
    await WithManagerAsync(async (manager, provider) =>
    {
        var notifications = 0;
        manager.Changed += (_, _) => throw new InvalidOperationException("Retired presentation");
        manager.Changed += (_, _) => notifications++;
        await manager.RefreshAsync();
        Require(manager.Packages.Single().State == DlcPackageState.Available,
            "A failed observer changed the inventory result.");
        await manager.DownloadAsync("fixture", consent: true);
        Require(provider.Installed && manager.Packages.Single().State == DlcPackageState.Installed,
            "A failed observer prevented or misclassified successful installation.");
        await manager.DeleteAsync("fixture");
        Require(!provider.Installed && manager.Packages.Single().State == DlcPackageState.Available,
            "A failed observer prevented or misclassified successful deletion.");
        Require(notifications >= 6, "A failing observer prevented later subscribers from running.");
    });
    Console.WriteLine("PASS transaction: observer faults preserve inventory, install and delete outcomes");
}

static async Task ProgressAsync()
{
    await WithManagerAsync(async (manager, provider) =>
    {
        provider.BlockDownload = true;
        var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += (_, _) => throw new InvalidOperationException("Retired presentation");
        manager.Changed += (_, _) =>
        {
            if (manager.Packages.Single().Transfer?.DownloadedBytes == 50) progress.TrySetResult();
        };
        var operation = manager.DownloadAsync("fixture", consent: true);
        try
        {
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Both reports retain the Transferring stage: the second notification
            // must pass through the real 200 ms ThreadPool Timer callback.
            provider.Progress!.Report(new(TrackType.File, 50, 100, Stage: DownloadStage.Transferring));
            await progress.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { provider.Complete.TrySetResult(); }
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Require(manager.Packages.Single().State == DlcPackageState.Installed,
            "Progress observers changed the successful transaction result.");
    });
    Console.WriteLine("PASS progress: timer observer fault is isolated and later subscribers receive progress");
}

static async Task CancellationAsync()
{
    await WithManagerAsync(async (manager, provider) =>
    {
        provider.BlockDownload = true;
        // Start with healthy observers, then fail exactly when cancellation is
        // requested. Notification failure must never skip the token cancellation.
        var operation = manager.DownloadAsync("fixture", consent: true);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.Changed += (_, _) => throw new InvalidOperationException("Retired presentation");
        try
        {
            manager.CancelDownload("fixture");
            Require(provider.Token.IsCancellationRequested, "The observer prevented cancellation.");
        }
        finally { provider.Complete.TrySetResult(); }
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!provider.Installed && !manager.IsBusy,
            "Cancelled download retained operation ownership or installed a package.");
        Require(manager.Packages.Single().WasCanceled, "Cancellation feedback was lost.");
    });
    Console.WriteLine("PASS cancel: observer failure cannot prevent token cancellation or retirement");
}

static async Task WithManagerAsync(Func<DlcManagerService, FixtureProvider, Task> scenario)
{
    var root = Path.Combine(Path.GetTempPath(), "DropSpace-DlcNotification-" + Guid.NewGuid().ToString("N"));
    try
    {
        var provider = new FixtureProvider();
        await using var manager = new DlcManagerService([provider], NullLogger<DlcManagerService>.Instance,
            new AppStoragePaths(root));
        await scenario(manager, provider);
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class FixtureProvider : IDlcPackageProvider
{
    public IReadOnlyList<DlcPackageDescriptor> Packages { get; } =
        [new("fixture", "Fixture", "Fixture", 100)];
    public event EventHandler? PackagesChanged { add { } remove { } }
    public bool Installed { get; private set; }
    public bool BlockDownload { get; set; }
    public CancellationToken Token { get; private set; }
    public IProgress<TrackProgress>? Progress { get; private set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<DlcPackageInspection> InspectAsync(string packageId, CancellationToken token) =>
        Task.FromResult(new DlcPackageInspection(Installed, Installed, Installed ? 100 : 0));
    public Task DownloadAsync(string packageId, bool consent, IProgress<double>? progress, CancellationToken token) =>
        throw new NotSupportedException("The real manager must use the transfer progress boundary.");
    public async Task DownloadWithProgressAsync(string packageId, bool consent, IProgress<TrackProgress>? progress, CancellationToken token)
    {
        Token = token;
        Progress = progress;
        progress!.Report(new(TrackType.File, 0, 100, Stage: DownloadStage.Transferring));
        Started.TrySetResult();
        if (BlockDownload) await Complete.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        Installed = true;
    }
    public Task DeleteAsync(string packageId, CancellationToken token)
    {
        Installed = false;
        return Task.CompletedTask;
    }
}
