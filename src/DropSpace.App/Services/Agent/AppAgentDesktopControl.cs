namespace DropSpace.App.Services.Agent;

/// <summary>Window callbacks are attached by the App after its real window exists.</summary>
public sealed class AppAgentDesktopControl
{
    private Action? _show, _hide, _exit;
    private Func<string, Task>? _settings;
    private Func<string, string, CancellationToken, Task<bool>>? _confirm;
    public bool IsAvailable => _show is not null;

    internal void Attach(Action show, Action hide, Func<string, Task> settings, Action exit,
        Func<string, string, CancellationToken, Task<bool>> confirm)
    { _show = show; _hide = hide; _settings = settings; _exit = exit; _confirm = confirm; }

    internal void Detach() { _show = _hide = _exit = null; _settings = null; _confirm = null; }

    public void Show() => (_show ?? throw new InvalidOperationException("The main window is not available."))();
    public void Hide() => (_hide ?? throw new InvalidOperationException("The main window is not available."))();
    public Task OpenSettingsAsync(string page) => (_settings ?? throw new InvalidOperationException("The settings window is not available."))(page);
    public void RequestExit() => (_exit ?? throw new InvalidOperationException("The main window is not available."))();
    public Task<bool> ConfirmAsync(string operation, string details, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (_confirm ?? throw new InvalidOperationException("The confirmation window is not available."))(operation, details, cancellationToken);
    }
}
