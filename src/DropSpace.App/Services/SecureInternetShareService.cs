using DropSpace.Core.Models;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Sharing;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DropSpace.App.Services;

public sealed class SecureInternetShareService(
    ShareCryptoService crypto,
    AppStoragePaths paths,
    ILogger<SecureInternetShareService> logger)
{
    private readonly ConcurrentDictionary<Guid, ShareBackendUploadSession> _sessions = new();
    private readonly InternetShareRevokeStore _revokeStore = new(paths);
    private int _initialized;

    public bool IsConfigured => TryGetEndpoint() is not null;

    public bool CanRevoke(Guid shareId) => _sessions.ContainsKey(shareId);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        var restored = await _revokeStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var handle in restored)
        {
            _sessions[handle.ShareId] = handle.Session;
        }

        logger.LogInformation(
            "Restored {RestoredCount} encrypted Internet Share revoke handle(s) without logging credentials.",
            restored.Count);
    }

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
        try
        {
            await _revokeStore.SaveAsync(
                result.Descriptor.ShareId,
                result.Session,
                result.Descriptor.ExpiresAtUtc,
                cancellationToken).ConfigureAwait(false);
            _sessions[result.Descriptor.ShareId] = result.Session;
            return result.Descriptor;
        }
        catch
        {
            // A share without a durable revoke handle would violate the user's control
            // guarantee. Best-effort revoke it before surfacing the persistence failure.
            try
            {
                await client.RevokeAsync(result.Session, result.Descriptor.ShareId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException) when (cleanupException is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                logger.LogError(
                    cleanupException,
                    "Encrypted Internet Share cleanup failed after local revoke-handle persistence failure for share {ShareId}.",
                    result.Descriptor.ShareId);
            }

            throw;
        }
    }

    public async Task<bool> RevokeAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(shareId, out var session)) return false;
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var backend = new CloudflareWorkerShareBackend(httpClient, session.RevokeUrl);
            await backend.RevokeAsync(session, shareId, cancellationToken).ConfigureAwait(false);
            await _revokeStore.DeleteAsync(shareId, cancellationToken).ConfigureAwait(false);
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
