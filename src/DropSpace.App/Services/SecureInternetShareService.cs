using DropSpace.Core.Models;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Sharing;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DropSpace.App.Services;

public sealed class SecureInternetShareService(
    ShareCryptoService crypto,
    ILogger<SecureInternetShareService> logger)
{
    private readonly ConcurrentDictionary<Guid, ShareBackendUploadSession> _sessions = new();

    public bool IsConfigured => TryGetEndpoint() is not null;

    public bool CanRevoke(Guid shareId) => _sessions.ContainsKey(shareId);

    public async Task<ShareDescriptor> CreateAsync(
        IReadOnlyList<ShareFileSource> sources,
        TimeSpan lifetime,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EnableInternetSharing) throw new InvalidOperationException("Internet Share is disabled in DropSpace settings.");
        var endpoint = TryGetEndpoint() ?? throw new InvalidOperationException("Internet Share requires DROPSPACE_SHARE_BACKEND_URL to point to an HTTPS Cloudflare Worker.");
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var backend = new CloudflareWorkerShareBackend(httpClient, endpoint);
        var client = new InternetShareClient(crypto, backend);
        logger.LogInformation("Starting encrypted Internet Share upload for {ItemCount} item(s) with expiry {Lifetime}.", sources.Count, lifetime);
        var result = await client.CreateWithSessionAsync(sources, lifetime, cancellationToken).ConfigureAwait(false);
        _sessions[result.Descriptor.ShareId] = result.Session;
        return result.Descriptor;
    }

    public async Task<bool> RevokeAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(shareId, out var session)) return false;
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var backend = new CloudflareWorkerShareBackend(httpClient, session.RevokeUrl);
            await backend.RevokeAsync(session, shareId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Keep the authenticated session available for an explicit retry. The
            // caller must never lose the only revocation handle after a transient error.
            _sessions[shareId] = session;
            throw;
        }
    }

    private static Uri? TryGetEndpoint()
    {
        var value = Environment.GetEnvironmentVariable("DROPSPACE_SHARE_BACKEND_URL");
        return Uri.TryCreate(value, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
            ? endpoint
            : null;
    }
}
