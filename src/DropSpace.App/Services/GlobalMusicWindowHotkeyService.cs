using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>Optional music action, reusing the existing native registration/lifetime implementation.</summary>
public sealed class GlobalMusicWindowHotkeyService : IDisposable, IAsyncDisposable
{
    private readonly GlobalQuickPanelHotkeyService _registration;
    public GlobalMusicWindowHotkeyService(ILogger<GlobalQuickPanelHotkeyService> logger) =>
        _registration = new(logger,0x4455,"DropSpace Music window hotkey");
    public event EventHandler? Invoked
    {
        add => _registration.Invoked += value;
        remove => _registration.Invoked -= value;
    }
    public bool IsRegistered => _registration.IsRegistered;
    public bool CanRegister(string gesture) => string.IsNullOrWhiteSpace(gesture) || _registration.CanRegister(gesture);
    public async Task<bool> TryStartAsync(string gesture,CancellationToken cancellationToken=default)
    {
        if(!string.IsNullOrWhiteSpace(gesture))return await _registration.TryStartAsync(gesture,cancellationToken).ConfigureAwait(false);
        await _registration.StopAsync(cancellationToken).ConfigureAwait(false);
        return !_registration.IsRegistered;
    }
    public async Task StartAsync(string gesture,CancellationToken cancellationToken=default)
    {
        if(string.IsNullOrWhiteSpace(gesture))await _registration.StopAsync(cancellationToken).ConfigureAwait(false);
        else await _registration.StartAsync(gesture,cancellationToken).ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken cancellationToken=default)=>_registration.StopAsync(cancellationToken);
    public void Dispose()=>_registration.Dispose();
    public ValueTask DisposeAsync()=>_registration.DisposeAsync();
}
