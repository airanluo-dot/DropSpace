using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
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
    private readonly SemaphoreSlim _startGate = new(1, 1);
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
        if (items.Select(item => item?.Id).Where(id => id is not null).Distinct().Count() != items.Count)
        {
            throw new InvalidDataException("Nearby share item identifiers must be unique.");
        }
        var total = 0L;
        foreach (var item in items)
        {
            ValidateItem(item);
            if (item.Length > _limits.InternetMaxBytes - total) throw new InvalidDataException("The nearby share size limit was exceeded.");
            total += item.Length;
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
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_app is not null) return;

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
        finally
        {
            _startGate.Release();
        }
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/s/{shareId:guid}/{token}", async (HttpContext context, Guid shareId, string token, CancellationToken cancellationToken) =>
        {
            if (!TryGetShare(shareId, token, context.Connection.RemoteIpAddress, out var share, out var error)) return Results.StatusCode(error);
            if (!share.TryRegisterReceiver(context.Connection.RemoteIpAddress)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var builder = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>DropSpace Nearby Share</title><h1>DropSpace Nearby Share</h1><ul>");
            foreach (var item in share.Items)
            {
                var safeName = EscapeHtml(item.DisplayName);
                var href = string.Concat("/s/", shareId.ToString("N"), "/", token, "/file/", item.Id.ToString("N"));
                builder.Append("<li><a download=\"").Append(safeName).Append("\" href=\"").Append(href).Append("\">").Append(safeName).Append("</a> <small>").Append(item.Length).Append(" bytes</small></li>");
            }
            builder.Append("</ul><p><strong>Security notice:</strong> this nearby link uses unencrypted HTTP on the private network. Anyone on that network who obtains the link can read these files. It expires automatically; use Internet Share for encrypted transport.</p>");
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-DropSpace-Transport"] = "unencrypted-private-http";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";
            return Results.Content(builder.ToString(), "text/html; charset=utf-8");
        });

        app.MapGet("/s/{shareId:guid}/{token}/file/{itemId:guid}", async (HttpContext context, Guid shareId, string token, Guid itemId, CancellationToken cancellationToken) =>
        {
            if (!TryGetShare(shareId, token, context.Connection.RemoteIpAddress, out var share, out var error)) return Results.StatusCode(error);
            if (!share.TryRegisterReceiver(context.Connection.RemoteIpAddress)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var item = share.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is null) return Results.NotFound();
            await using var stream = await item.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            var total = stream.CanSeek ? stream.Length : item.Length;
            if (total != item.Length) return Results.StatusCode(StatusCodes.Status409Conflict);
            var start = 0L;
            var end = total - 1;
            var partial = false;
            var range = context.Request.Headers.Range.ToString();
            if (range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && ParseRange(range[6..], total, out start, out end))
            {
                partial = true;
                if (stream.CanSeek) stream.Position = start;
                else await SkipExactlyAsync(stream, start, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(range))
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers["Content-Range"] = string.Concat("bytes */", total);
                return Results.Empty;
            }

            context.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
            context.Response.ContentType = item.MimeType;
            context.Response.ContentLength = end - start + 1;
            context.Response.Headers["Content-Disposition"] = string.Concat("attachment; filename=\"", EscapeHeader(item.DisplayName), "\"");
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-DropSpace-Transport"] = "unencrypted-private-http";
            if (partial) context.Response.Headers["Content-Range"] = string.Concat("bytes ", start, "-", end, "/", total);
            await CopyExactlyAsync(stream, context.Response.Body, end - start + 1, cancellationToken).ConfigureAwait(false);
            return Results.Empty;
        });
    }

    private bool TryGetShare(
        Guid shareId,
        string token,
        IPAddress? remoteAddress,
        out NearbyShare share,
        out int error)
    {
        error = StatusCodes.Status404NotFound;
        share = null!;
        if (remoteAddress is null || !IsPrivate(remoteAddress))
        {
            error = StatusCodes.Status403Forbidden;
            return false;
        }

        if (!_shares.TryGetValue(shareId, out var candidate) ||
            candidate is null ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(candidate.Token),
                Encoding.UTF8.GetBytes(token)))
        {
            share = null!;
            return false;
        }

        share = candidate;

        if (share.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _shares.TryRemove(shareId, out _);
            error = StatusCodes.Status410Gone;
            share = null!;
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

    private static async Task SkipExactlyAsync(Stream input, long bytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        var remaining = bytes;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Nearby share source changed during range download.");
            remaining -= read;
        }
    }

    private static bool ParseRange(string value, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        var parts = value.Split('-', 2);
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start))
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, total - suffix);
        }
        if (!string.IsNullOrWhiteSpace(parts[1]) && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var requestedEnd)) end = Math.Min(requestedEnd, total - 1);
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
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void ValidateItem(NearbyShareItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.DisplayName) || item.DisplayName.Length > 512 ||
            item.DisplayName.Any(char.IsControl) || string.IsNullOrWhiteSpace(item.MimeType) || item.MimeType.Length > 128 ||
            item.MimeType.Any(character => character is '\r' or '\n') || item.Length < 0)
        {
            throw new InvalidDataException("A nearby share item is invalid.");
        }
        ArgumentNullException.ThrowIfNull(item.OpenReadAsync);
    }
    private static string EscapeHtml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
    private static string EscapeHeader(string value) => new(value.Where(character => character is >= ' ' and <= '~' && character is not '"' and not '\\').ToArray());

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _shares.Clear();
            var app = _app;
            _app = null;
            _baseUri = null;
            if (app is not null)
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _startGate.Release();
        }
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
