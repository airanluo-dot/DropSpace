using System.Reflection;
using DropSpace.App.Services.Media;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Media.Control;

var selected = args.Length == 0 ? new[] { "startup", "recovery", "native" } : args;
if (selected.Any(value => value is not ("startup" or "recovery" or "native" or "image-native")))
    throw new ArgumentException("Choose startup, recovery, native or image-native.");
if (selected.Contains("startup")) await StartupAndToggleAsync();
if (selected.Contains("recovery")) await FailedControlAndLateReadAsync();
if (selected.Contains("native")) await NativeOwnerRetirementAsync();
if (selected.Contains("image-native")) await ImageNativeScenario.RunAsync();
Console.WriteLine($"PASS {selected.Length} selected production-source scenarios (controlled platform shims; no Windows/NetEase execution).");

static async Task StartupAndToggleAsync()
{
    var manager = new GlobalSystemMediaTransportControlsSessionManager();
    var existing = new GlobalSystemMediaTransportControlsSession(manager.Access, "existing");
    existing.SourceUnavailable = true;
    manager.Sessions = [existing];
    GlobalSystemMediaTransportControlsSessionManager.Next = manager;
    await using var service = NewService();
    var initialRead = Signal();
    service.Changed += InitialRead;
    void InitialRead(object? sender, MediaSessionSnapshot snapshot) => initialRead.TrySetResult();
    await service.SetEnabledAsync(true);
    await initialRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
    service.Changed -= InitialRead;
    Check(!service.Current.IsActive, "unready source must remain undiscovered");
    existing.SourceUnavailable = false;
    existing.Changed(); // No manager event: ready metadata callback is the only recovery trigger.
    await SnapshotAsync(service, snapshot => snapshot.TrackTitle == "existing");
    Check(service.IsAvailable, "initial session must be discovered without a manager event");
    await FourControlsAsync(service, existing);

    await service.SetEnabledAsync(false);
    var nextManager = new GlobalSystemMediaTransportControlsSessionManager();
    GlobalSystemMediaTransportControlsSessionManager.Next = nextManager;
    await service.SetEnabledAsync(true);
    Check(!service.Current.IsActive, "empty manager must not retain old metadata");
    var reenabled = new GlobalSystemMediaTransportControlsSession(nextManager.Access, "reenabled");
    nextManager.Sessions = [reenabled];
    nextManager.Changed();
    await SnapshotAsync(service, snapshot => snapshot.TrackTitle == "reenabled");
    await FourControlsAsync(service, reenabled);
    Check(existing.Commands.SequenceEqual(new[] { "pause", "play", "previous", "next" }), "old publisher received commands after re-enable");
    Console.WriteLine("PASS startup transient-source/session-ready and manager-event discovery, disable-enable, pause/play/previous/next delivered");
}

static async Task FailedControlAndLateReadAsync()
{
    var manager = new GlobalSystemMediaTransportControlsSessionManager();
    var old = new GlobalSystemMediaTransportControlsSession(manager.Access, "old");
    manager.Sessions = [old];
    GlobalSystemMediaTransportControlsSessionManager.Next = manager;
    await using var service = NewService();
    await service.SetEnabledAsync(true);
    await SnapshotAsync(service, snapshot => snapshot.TrackTitle == "old");
    var replacement = new GlobalSystemMediaTransportControlsSession(manager.Access, "replacement");
    manager.Sessions = [old, replacement]; // A disconnected OS-current wrapper remains enumerated after plugin re-enable.
    old.Disconnected = true;
    try { await service.SkipNextAsync(); throw new Exception("Rejected control must remain visible to caller."); }
    catch (System.Runtime.InteropServices.COMException exception) when (exception.HResult == unchecked((int)0x80010108)) { }
    await SnapshotAsync(service, snapshot => snapshot.TrackTitle == "replacement");
    await service.SkipNextAsync();
    Check(old.Commands.SequenceEqual(new[] { "next" }) && replacement.Commands.SequenceEqual(new[] { "next" }),
        "failed control must rediscover without replaying the failed command");

    var staleSessionCallback = replacement.CaptureMetadataChanged();
    var staleManagerCallback = manager.CaptureChanged();
    replacement.ReadEntered = Signal();
    replacement.ReleaseRead = Signal();
    replacement.ReadCompleted = Signal();
    replacement.Changed();
    await replacement.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await service.SetEnabledAsync(false);
    var newestManager = new GlobalSystemMediaTransportControlsSessionManager();
    var newest = new GlobalSystemMediaTransportControlsSession(newestManager.Access, "newest");
    newestManager.Sessions = [newest];
    GlobalSystemMediaTransportControlsSessionManager.Next = newestManager;
    await service.SetEnabledAsync(true);
    await SnapshotAsync(service, snapshot => snapshot.TrackTitle == "newest");
    var revision = Revision(service);
    staleSessionCallback();
    staleManagerCallback();
    Check(Revision(service) == revision, "retired callbacks invalidated a new lifetime");
    replacement.ReleaseRead.TrySetResult();
    await replacement.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
    await FourControlsAsync(service, newest);
    Check(service.Current.TrackTitle == "newest", "late old metadata overwrote the new lifetime");
    Console.WriteLine("PASS disconnected control rediscovery, no command replay, retired callbacks and late metadata isolated");
}

static async Task NativeOwnerRetirementAsync()
{
    var owner = new MediaNativeContext();
    var budget = new BoundedMediaOperation(executor: owner);
    var entered = Signal();
    var release = Signal();
    var completed = Signal();
    using var stop = new CancellationTokenSource();
    var thread = 0;
    var pending = budget.RunAsync(new object(), async _ =>
    {
        thread = Environment.CurrentManagedThreadId;
        entered.TrySetResult();
        await release.Task;
        await Task.Yield();
        Check(thread == Environment.CurrentManagedThreadId, "native await left its owner thread");
        completed.TrySetResult();
        return true;
    }, TimeSpan.FromSeconds(3), stop.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    stop.Cancel();
    try { await pending; throw new Exception("canceled caller unexpectedly completed"); }
    catch (OperationCanceledException) { }
    Check(budget.Outstanding == 1, "cancellation dropped still-running native ownership");
    var queued = owner.RunAsync(async () => { await Task.Yield(); Check(thread == Environment.CurrentManagedThreadId, "queued work moved owner"); return true; });
    var shutdown = owner.DisposeAsync().AsTask();
    Check(!shutdown.IsCompleted, "owner shut down while admitted native operation remained active");
    release.TrySetResult();
    await Task.WhenAll(completed.Task, queued, shutdown, budget.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(3));
    Check(budget.Outstanding == 0, "native budget did not drain");
    try { await owner.RunAsync(() => Task.FromResult(true)); throw new Exception("retired owner accepted new work"); }
    catch (ObjectDisposedException) { }
    await owner.DisposeAsync();
    Microsoft.UI.Dispatching.DispatcherQueueController.FailNextStartup = 1;
    var starts = Microsoft.UI.Dispatching.DispatcherQueueController.Starts;
    await using var retryOwner = new MediaNativeContext();
    try { await retryOwner.RunAsync(() => Task.FromResult(true)); throw new Exception("controlled startup failure did not reach caller"); }
    catch (InvalidOperationException) { }
    Check(await retryOwner.RunAsync(() => Task.FromResult(true)), "explicit retry after startup failure failed");
    Check(Microsoft.UI.Dispatching.DispatcherQueueController.Starts == starts + 2, "startup retry did not create exactly one replacement");
    try { await retryOwner.RunAsync<bool>(() => throw new IOException("Controlled operation failure")); }
    catch (IOException) { }
    await retryOwner.RunAsync(() => Task.FromResult(true));
    Check(Microsoft.UI.Dispatching.DispatcherQueueController.Starts == starts + 2, "operation failure replaced an established native owner");
    Console.WriteLine("PASS native await affinity, cancellation/retirement ownership, explicit startup retry without replacing a live owner");
}

static WindowsMediaSessionService NewService() => new(NullLogger<WindowsMediaSessionService>.Instance);
static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static long Revision(WindowsMediaSessionService service) =>
    (long)typeof(WindowsMediaSessionService).GetField("_metadataRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task SnapshotAsync(WindowsMediaSessionService service, Func<MediaSessionSnapshot, bool> predicate)
{
    var completion = Signal();
    void Changed(object? sender, MediaSessionSnapshot snapshot) { if (predicate(snapshot)) completion.TrySetResult(); }
    service.Changed += Changed;
    try
    {
        if (predicate(service.Current)) return;
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    finally { service.Changed -= Changed; }
}
static async Task FourControlsAsync(WindowsMediaSessionService service, GlobalSystemMediaTransportControlsSession publisher)
{
    await service.PlayPauseAsync();
    Check(!publisher.Playing, "pause was accepted but not executed");
    await SnapshotAsync(service, snapshot => snapshot.PlaybackState == MediaPlaybackState.Paused);
    await service.PlayPauseAsync();
    Check(publisher.Playing, "play was accepted but not executed");
    await SnapshotAsync(service, snapshot => snapshot.PlaybackState == MediaPlaybackState.Playing);
    var track = publisher.TrackIndex;
    await service.SkipPreviousAsync();
    Check(publisher.TrackIndex == track - 1, "previous was accepted but not executed");
    await SnapshotAsync(service, snapshot => snapshot.TrackTitle == ExpectedTitle(publisher));
    await service.SkipNextAsync();
    Check(publisher.TrackIndex == track, "next was accepted but not executed");
    await SnapshotAsync(service, snapshot => snapshot.TrackTitle == ExpectedTitle(publisher));
    Check(publisher.Commands.TakeLast(4).SequenceEqual(new[] { "pause", "play", "previous", "next" }),
        "controls did not reach and execute on the expected publisher");
}

static string ExpectedTitle(GlobalSystemMediaTransportControlsSession publisher) =>
    publisher.Title + (publisher.TrackIndex == 0 ? "" : "-" + publisher.TrackIndex);
