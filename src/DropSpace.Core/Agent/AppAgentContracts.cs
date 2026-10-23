using System.Text.Json;

namespace DropSpace.Core.Agent;

public sealed record AppAgentCommand(string Name, string Description, JsonElement Parameters, bool Mutates = false);

/// <summary>Explicit allowlisted application operations. The host never reflects into the App or its container.</summary>
public interface IAppAgentDispatcher
{
    IReadOnlyList<AppAgentCommand> Catalog { get; }
    Task<JsonElement> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken = default);
}

public sealed class AppAgentCommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum AppAgentBindingState { Pending = 1, Paired = 2 }

/// <summary>UI-safe metadata. Neither pairing keys nor bearer credentials appear in this projection.</summary>
public sealed record AppAgentBinding(Guid Id, string Name, AppAgentBindingState State,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? PairedAtUtc, DateTimeOffset? LastUsedAtUtc);

/// <summary>The plaintext pairing key is returned only by explicit local UI creation.</summary>
public sealed record AppAgentPairingInvitation(Guid BindingId, Uri Endpoint, string PairingKey, DateTimeOffset ExpiresAtUtc);

public interface IAppAgentHost : IAsyncDisposable
{
    event EventHandler? Changed;
    Uri? Endpoint { get; }
    string DiscoveryFilePath { get; }
    IReadOnlyList<AppAgentBinding> Bindings { get; }
    string? ErrorCode { get; }
    // Startup remains inactive when there is no unexpired pending or paired grant.
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<AppAgentPairingInvitation> CreateBindingAsync(string name, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(Guid bindingId, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
