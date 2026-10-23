using DropSpace.App.Services.Agent;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Agent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views.Settings;

/// <summary>Local authorization ownership stays in the native App, outside the Agent command API.</summary>
public sealed class AgentConnectionsPanel : StackPanel
{
    private readonly IAppAgentHost _host;
    private readonly AgentSkillPackageService _skill;
    private readonly IAppStringLocalizer _strings;
    private readonly TextBox _name = new() { MaxLength = 64, MinWidth = 240 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _invitationStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _bindings = new() { Spacing = 16 };
    private readonly Button _create = new();
    private readonly Button _copy = new() { IsEnabled = false };
    private AppAgentPairingInvitation? _invitation;
    private bool _loaded, _busy;

    public AgentConnectionsPanel(IAppAgentHost host, AgentSkillPackageService skill, IAppStringLocalizer strings, Func<string, Task> copyInvitation)
    {
        _host = host; _skill = skill; _strings = strings; Spacing = 16;
        Children.Add(new TextBlock { Text = strings.Get("AgentConnectionsTitle"), FontSize = 22 });
        Children.Add(new TextBlock { Text = strings.Get("AgentConnectionsDescription"), TextWrapping = TextWrapping.Wrap });
        Children.Add(_status);
        Children.Add(new TextBlock { Text = strings.Get("AgentAccessDescription"), TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        _name.Header = strings.Get("AgentNameLabel"); _name.PlaceholderText = strings.Get("AgentNamePlaceholder");
        AutomationProperties.SetAutomationId(_name, "AgentBindingName");
        _create.Content = strings.Get("AgentCreateKey"); _copy.Content = strings.Get("AgentCopyInstallPrompt");
        AutomationProperties.SetAutomationId(_create, "AgentCreateBinding");
        AutomationProperties.SetAutomationId(_copy, "AgentCopyInstallPrompt");
        Children.Add(_name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_create); buttons.Children.Add(_copy); Children.Add(buttons);
        Children.Add(_invitationStatus); Children.Add(_notice); Children.Add(_bindings);
        Children.Add(new TextBlock { Text = strings.Get("AgentRevocationDescription"), TextWrapping = TextWrapping.Wrap, Opacity = .75 });
        _create.Click += async (_, _) => await RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) throw new ArgumentException(strings.Get("AgentNameRequired"));
            _invitation = await _host.CreateBindingAsync(_name.Text.Trim());
            _name.Text = string.Empty;
        });
        _copy.Click += async (_, _) => await RunAsync(async () =>
        {
            var invitation = _invitation;
            if (invitation is null || invitation.ExpiresAtUtc <= DateTimeOffset.UtcNow) return;
            var prompt = await _skill.CreateInstallPromptAsync(invitation, _host.DiscoveryFilePath);
            if (_invitation != invitation || !_host.Bindings.Any(binding => binding.Id == invitation.BindingId && binding.State == AppAgentBindingState.Pending))
                throw new InvalidOperationException(strings.Get("AgentInvitationExpired"));
            await copyInvitation(prompt);
            _notice.Text = strings.Get("AgentPromptCopied");
        });
        Loaded += async (_, _) =>
        {
            _loaded = true; _host.Changed += OnChanged;
            await RunAsync(() => _host.StartAsync());
        };
        Unloaded += (_, _) => { _loaded = false; _host.Changed -= OnChanged; _invitation = null; };
    }

    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() => { if (_loaded && !_busy) Refresh(); });

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; _create.IsEnabled = _copy.IsEnabled = false; _notice.Text = string.Empty;
        try { await operation(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _notice.Text = _strings.Format("AgentOperationFailed", error.Message); }
        finally { _busy = false; if (_loaded) Refresh(); }
    }

    private void Refresh()
    {
        var bindings = _host.Bindings;
        if (_invitation is { } invitation && (invitation.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            !bindings.Any(binding => binding.Id == invitation.BindingId && binding.State == AppAgentBindingState.Pending))) _invitation = null;
        _status.Text = _host.ErrorCode is { } code ? _strings.Format("AgentServiceError", code) :
            _host.Endpoint is { } endpoint ? _strings.Format("AgentListening", endpoint.GetLeftPart(UriPartial.Authority)) : _strings.Get("AgentInactive");
        _create.IsEnabled = !_busy && _host.ErrorCode is null;
        _copy.IsEnabled = !_busy && _invitation is not null;
        _invitationStatus.Text = _invitation is { } current
            ? _strings.Format("AgentInvitationReady", current.ExpiresAtUtc.ToLocalTime().ToString("t", _strings.Culture)) : string.Empty;
        _invitationStatus.Visibility = string.IsNullOrEmpty(_invitationStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        _notice.Visibility = string.IsNullOrEmpty(_notice.Text) ? Visibility.Collapsed : Visibility.Visible;
        _bindings.Children.Clear();
        if (bindings.Count == 0) _bindings.Children.Add(new TextBlock { Text = _strings.Get("AgentNoBindings"), Opacity = .7 });
        foreach (var binding in bindings)
        {
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(new TextBlock { Text = binding.Name, FontSize = 16, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = _strings.Get(binding.State == AppAgentBindingState.Paired ? "AgentPaired" : "AgentPending"), Opacity = .7 });
            var date = binding.LastUsedAtUtc ?? binding.PairedAtUtc ?? binding.CreatedAtUtc;
            row.Children.Add(new TextBlock { Text = _strings.Format("AgentLastActivity", date.ToLocalTime().ToString("g", _strings.Culture)), Opacity = .7 });
            var revoke = new Button { Content = _strings.Get("AgentRevoke"), IsEnabled = !_busy };
            AutomationProperties.SetName(revoke, _strings.Get("AgentRevoke") + " " + binding.Name);
            revoke.Click += async (_, _) => await RunAsync(async () =>
            {
                await _host.RevokeAsync(binding.Id);
                if (_invitation?.BindingId == binding.Id) _invitation = null;
                _notice.Text = _strings.Get("AgentRevoked");
            });
            row.Children.Add(revoke); _bindings.Children.Add(row);
        }
    }
}
