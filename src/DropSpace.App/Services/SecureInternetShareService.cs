using DropSpace.Core.Models;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Sharing;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed class SecureInternetShareService(
    ShareCryptoService crypto,
    ILogger<SecureInternetShareService> logger)
{
    public bool IsConfigured => TryGetEndpoint() is not null;

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
        return await client.CreateAsync(sources, lifetime, cancellationToken).ConfigureAwait(false);
    }

    private static Uri? TryGetEndpoint()
    {
        var value = Environment.GetEnvironmentVariable("DROPSPACE_SHARE_BACKEND_URL");
        return Uri.TryCreate(value, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps ? endpoint : null;
    }
}
