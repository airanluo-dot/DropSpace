using System.Reflection;
using DropSpace.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

var budget = TimeSpan.FromSeconds(5);
// One fixture worker makes queued gate continuations deterministic: disposal's
// synchronous continuation finishes before the next Stop waiter resumes.
if (!ThreadPool.SetMinThreads(1, 1) || !ThreadPool.SetMaxThreads(1, 1))
    throw new InvalidOperationException("Could not establish the fixture scheduler.");
await Task.Run(QuickPanelStopAsync);
await Task.Run(MusicStopAsync);

async Task QuickPanelStopAsync()
{
    var hotkey = new GlobalQuickPanelHotkeyService(NullLogger<GlobalQuickPanelHotkeyService>.Instance);
    var gate = GetGate(hotkey);
    await gate.WaitAsync();
    // Queue disposal's Stop before a second, already admitted stop. No native
    // message thread is started: the defect is in managed gate retirement.
    var disposal = hotkey.DisposeAsync().AsTask();
    var queuedStop = hotkey.StopAsync();
    gate.Release();
    await Task.WhenAll(disposal, queuedStop).WaitAsync(budget);
    await hotkey.StopAsync().WaitAsync(budget);
    Require(ReferenceEquals(disposal, hotkey.DisposeAsync().AsTask()), "Disposal completion must be shared.");
    try
    {
        await hotkey.TryStartAsync("Win+Shift+Space");
        throw new InvalidOperationException("Disposed registration accepted a new start.");
    }
    catch (ObjectDisposedException) { }
    Console.WriteLine("PASS Quick Panel: queued/repeated stops settle through shared disposal; new starts stay rejected");
}

async Task MusicStopAsync()
{
    var music = new GlobalMusicWindowHotkeyService(NullLogger<GlobalQuickPanelHotkeyService>.Instance);
    var registration = (GlobalQuickPanelHotkeyService)(typeof(GlobalMusicWindowHotkeyService)
        .GetField("_registration", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(music)
        ?? throw new InvalidOperationException("Music registration boundary changed."));
    var gate = GetGate(registration);
    await gate.WaitAsync();
    var disposal = music.DisposeAsync().AsTask();
    var queuedStop = music.StopAsync();
    gate.Release();
    await Task.WhenAll(disposal, queuedStop).WaitAsync(budget);
    // An unbound music action follows the same unregister path.
    Require(await music.TryStartAsync(string.Empty), "Unbound music action did not settle its stop.");
    Require(!music.IsRegistered, "Stopped music action reported a live registration.");
    Console.WriteLine("PASS Music: wrapper's queued stop and unbound-action path remain safe after disposal");
}

static SemaphoreSlim GetGate(GlobalQuickPanelHotkeyService hotkey) =>
    (SemaphoreSlim)(typeof(GlobalQuickPanelHotkeyService)
        .GetField("_lifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(hotkey)
        ?? throw new InvalidOperationException("Hotkey lifecycle boundary changed."));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
