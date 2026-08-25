using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Transfer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Sharing;

public sealed record NearbyShareItem(
    Guid Id,
    string DisplayName,
    string MimeType,
    long Length,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);

public sealed class NearbyShareServer(ShareLimits? limits = null) : IAsyncDisposable
{
    private readonly ShareLimits _limits = (limits ?? new ShareLimits()).Validate();
    private readonly ConcurrentDictionary<Guid, NearbyShare> _shares = new();
    private WebApplication? _app;
    private Uri? _baseUri;
    private int _disposed;

    public Uri? BaseUri => _baseUri;

    public async Task<ShareDescriptor> CreateShareAsync(
        IReadOnlyList<NearbyShareItem> items,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 || items.Count > _limits.InternetMaxItems) throw new ArgumentOutOfRangeException(nameof(items));
        if (items.Any(item => item.Length < 0)) throw new InvalidDataException("A nearby share item length cannot be negative.");
        var total = items.Sum(item => item.Length);
        if (total < 0 || items.Any(item => item.Length < 0) || total > _limits.InternetMaxBytes)
        {
            throw new InvalidDataException("The nearby share size limit was exceeded.");
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var shareId = Guid.NewGuid();
        var token = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(_limits.NearbyTokenBytes));
        var requestedLifetime = lifetime ?? TimeSpan.FromMinutes(_limits.NearbyTtlMinutes);
        if (requestedLifetime <= TimeSpan.Zero || requestedLifetime > TimeSpan.FromMinutes(_limits.NearbyTtlMinutes)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var expires = DateTimeOffset.UtcNow.Add(requestedLifetime);
        _shares[shareId] = new NearbyShare(shareId, token, expires, items.ToArray(), _limits.MaxNearbyReceivers);
        var url = new Uri(string.Concat(_baseUri, "s/", shareId.ToString("N"), "/", token));
        return new ShareDescriptor(shareId, url, expires, items.Count, total, false, null);
    }

    public bool Revoke(Guid shareId) => _shares.TryRemove(shareId, out _);

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) return;
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var privateAddress = GetPrivateAddress();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(NearbyShareServer).Assembly.GetName().Name ?? "DropSpace",
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Parse(privateAddress), 0));
        var app = builder.Build();
        MapRoutes(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address, UriKind.Absolute, out var bound))
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Nearby share server did not expose a bound endpoint.");
        }

        _baseUri = new Uri(string.Concat("http://", privateAddress, ":", bound.Port, "/"));
        _app = app;
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/s/{shareId:guid}/{token}", async (HttpContext context, Guid shareId, string token, CancellationToken cancellationToken) =>
        {
            if (!TryGetShare(shareId, token, context, out var share, out var error)) return Results.StatusCode(error);
            if (!share.TryRegisterReceiver(context.Connection.RemoteIpAddress)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var builder = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>DropSpace Nearby Share</title><h1>DropSpace Nearby Share</h1><ul>");
            foreach (var item in share.Items)
            {
                var safeName = EscapeHtml(item.DisplayName);
                var href = string.Concat("/s/", shareId.ToString("N"), "/", token, "/file/", item.Id.ToString("N"));
                builder.Append("<li><a download=\"").Append(safeName).Append("\" href=\"").Append(href).Append("\">").Append(safeName).Append("</a> <small>").Append(item.Length).Append(" bytes</small></li>");
            }
            builder.Append("</ul><p>This link is local-network only and expires automatically.</p>");
            return Results.Content(builder.ToString(), "text/html; charset=utf-8");
        });

        app.MapGet("/s/{shareId:guid}/{token}/file/{itemId:guid}", async (HttpContext context, Guid shareId, string token, Guid itemId, CancellationToken cancellationToken) =>
        {
            if (!TryGetShare(shareId, token, context, out var share, out var error)) return Results.StatusCode(error);
            if (!share.TryRegisterReceiver(context.Connection.RemoteIpAddress)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var item = share.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is null) return Results.NotFound();
            await using var stream = await item.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            var total = stream.CanSeek ? stream.Length : item.Length;
            var start = 0L;
            var end = total - 1;
            var partial = false;
            var range = context.Request.Headers.Range.ToString();
            if (range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && ParseRange(range[6..], total, out start, out end))
            {
                partial = true;
                if (stream.CanSeek) stream.Position = start;
            }
            else if (!string.IsNullOrWhiteSpace(range))
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers["Content-Range"] = string.Concat("bytes */", total);
                return Results.Ok();
            }

            context.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
            context.Response.ContentType = item.MimeType;
            context.Response.ContentLength = end - start + 1;
            context.Response.Headers["Content-Disposition"] = string.Concat("attachment; filename=\"", EscapeHeader(item.DisplayName), "\"");
            if (partial) context.Response.Headers["Content-Range"] = string.Concat("bytes ", start, "-", end, "/", total);
            await CopyExactlyAsync(stream, context.Response.Body, end - start + 1, cancellationToken).ConfigureAwait(false);
            return Results.Ok();
        });
    }

    private bool TryGetShare(Guid shareId, string token, HttpContext context, out NearbyShare share, out int error)
    {
        error = StatusCodes.Status404NotFound;
        if (!_shares.TryGetValue(shareId, out share!) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(share.Token), Encoding.UTF8.GetBytes(token))) return false;
        if (share.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _shares.TryRemove(shareId, out _);
            error = StatusCodes.Status410Gone;
            return false;
        }
        return true;
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, long bytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        var remaining = bytes;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Nearby share source changed during download.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static bool ParseRange(string value, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        var parts = value.Split('-', 2);
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], out start))
        {
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, total - suffix);
        }
        if (!string.IsNullOrWhiteSpace(parts[1]) && long.TryParse(parts[1], out var requestedEnd)) end = requestedEnd;
        return start >= 0 && start < total && end >= start && end < total;
    }

    private static string GetPrivateAddress()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .OfType<IPAddress>()
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();
        return addresses.FirstOrDefault(IsPrivate)?.ToString()
            ?? throw new InvalidOperationException("Nearby sharing requires an active private-network IPv4 address.");
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string EscapeHtml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
    private static string EscapeHeader(string value) => new(value.Where(character => character is >= ' ' and <= '~' && character is not '"' and not '\\').ToArray());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shares.Clear();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        _app = null;
        _baseUri = null;
    }

    private sealed class NearbyShare(Guid id, string token, DateTimeOffset expiresAtUtc, IReadOnlyList<NearbyShareItem> items, int maxReceivers)
    {
        private readonly ConcurrentDictionary<string, byte> _receivers = new(StringComparer.Ordinal);
        public Guid Id { get; } = id;
        public string Token { get; } = token;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public IReadOnlyList<NearbyShareItem> Items { get; } = items;
        public bool TryRegisterReceiver(IPAddress? address)
        {
            var key = address?.ToString() ?? "unknown";
            if (_receivers.ContainsKey(key)) return true;
            if (_receivers.Count >= maxReceivers) return false;
            return _receivers.TryAdd(key, 0);
        }
    }
}
