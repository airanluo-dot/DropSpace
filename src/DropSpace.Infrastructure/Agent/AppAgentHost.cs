using System.Net;
using System.Text.Json;
using DropSpace.Core.Agent;
using DropSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace DropSpace.Infrastructure.Agent;

public sealed class AppAgentHost : IAppAgentHost
{
    public const int ProtocolVersion = 1;
    public const int MaximumBodyBytes = 1_048_576;
    public const int MaximumResponseBytes = 4_194_304;
    private readonly IAppAgentDispatcher _dispatcher;
    private readonly ILogger<AppAgentHost> _logger;
    private readonly TimeProvider _clock;
    private readonly AgentAuthorizationStore _authorization;
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _requests = new(4, 4);
    private readonly Queue<DateTimeOffset> _pairingAttempts = new();
    private readonly object _pairingGate = new();
    private CancellationTokenSource? _stop;
    private WebApplication? _app;
    private bool _disposed;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AppAgentHost(AppStoragePaths paths, IAppAgentDispatcher dispatcher, ILogger<AppAgentHost> logger, TimeProvider? clock = null)
    {
        _dispatcher = dispatcher; _logger = logger; _clock = clock ?? TimeProvider.System;
        _authorization = new AgentAuthorizationStore(paths.Agent, _clock);
        DiscoveryFilePath = Path.Combine(paths.Agent, "endpoint.json");
    }
    public event EventHandler? Changed;
    public Uri? Endpoint { get; private set; }
    public string DiscoveryFilePath { get; }
    public IReadOnlyList<AppAgentBinding> Bindings => _authorization.Bindings;
    public string? ErrorCode { get; private set; }
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan ExpiryCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _authorization.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (Bindings.Any(binding => binding.State == AppAgentBindingState.Paired || binding.ExpiresAtUtc > _clock.GetUtcNow()))
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            else RemoveDiscovery();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { ErrorCode = "authorization_store_unavailable"; RaiseChanged(); throw; }
        finally { _lifecycle.Release(); }
    }

    public async Task<AppAgentPairingInvitation> CreateBindingAsync(string name, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _authorization.LoadAsync(cancellationToken).ConfigureAwait(false);
            var created = await _authorization.CreateAsync(name, cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            RaiseChanged();
            return new(created.Binding.Id, Endpoint!, created.Key, created.Binding.ExpiresAtUtc!.Value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { ErrorCode = "authorization_store_unavailable"; RaiseChanged(); throw; }
        finally { _lifecycle.Release(); }
    }

    public async Task<bool> RevokeAsync(Guid bindingId, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _authorization.LoadAsync(cancellationToken).ConfigureAwait(false);
            var removed = await _authorization.RevokeAsync(bindingId, cancellationToken).ConfigureAwait(false);
            if (Bindings.Count == 0) await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            RaiseChanged();
            return removed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { ErrorCode = "authorization_store_unavailable"; RaiseChanged(); throw; }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) return;
        _authorization.PrepareDirectory();
        if (new FileInfo(DiscoveryFilePath).LinkTarget is not null ||
            File.Exists(DiscoveryFilePath) && (File.GetAttributes(DiscoveryFilePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked agent discovery files are not supported.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        { ApplicationName = typeof(AppAgentHost).Assembly.GetName().Name, EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.MaxRequestBodySize = MaximumBodyBytes;
            options.Limits.MaxRequestHeadersTotalSize = 16_384;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxRequestLineSize = 4096;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        });
        var app = builder.Build();
        _stop = new CancellationTokenSource();
        app.Run(HandleRequestAsync);
        _app = app;
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var bound) || bound.Host != "127.0.0.1")
                throw new InvalidOperationException("Agent host did not expose its loopback endpoint.");
            Endpoint = bound;
            var temporary = Path.Combine(Path.GetDirectoryName(DiscoveryFilePath)!, $".endpoint-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                    await JsonSerializer.SerializeAsync(output, new { protocolVersion = ProtocolVersion,
                        endpoint = Endpoint.AbsoluteUri, processId = Environment.ProcessId }, Json, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, DiscoveryFilePath, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            ErrorCode = null;
            _ = ExpireInvitationsAsync(_stop.Token);
        }
        catch { await StopCoreAsync(CancellationToken.None).ConfigureAwait(false); throw; }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        var endpoint = Endpoint;
        if (endpoint is null || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            context.Request.Host.Value != endpoint.Authority ||
            context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Referer") ||
            context.Request.Headers.Keys.Any(key => key.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase)))
        { await ErrorAsync(context, 403, "forbidden_origin").ConfigureAwait(false); return; }
        if (!await _requests.WaitAsync(0).ConfigureAwait(false))
        { await ErrorAsync(context, 429, "busy").ConfigureAwait(false); return; }
        var releaseRequest = true;
        AgentAuthorizationStore.AuthorizationLease? authorization = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop?.Token ?? CancellationToken.None);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            var path = context.Request.Path.Value;
            if (context.Request.Method == "GET" && path == "/v1/health")
            { await JsonAsync(context, new { protocolVersion = ProtocolVersion, status = "ready" }, deadline.Token).ConfigureAwait(false); return; }
            if (context.Request.Method == "POST" && path == "/v1/pair")
            {
                if (!AdmitPairAttempt()) { await ErrorAsync(context, 429, "pairing_rate_limited").ConfigureAwait(false); return; }
                using var body = await ReadBodyAsync(context, deadline.Token).ConfigureAwait(false);
                RequireProperties(body.RootElement, "bindingId", "pairingKey");
                if (!body.RootElement.GetProperty("bindingId").TryGetGuid(out var id) ||
                    body.RootElement.GetProperty("pairingKey").ValueKind != JsonValueKind.String)
                    throw new AppAgentCommandException("invalid_request", "Invalid pairing request.");
                var token = await _authorization.PairAsync(id, body.RootElement.GetProperty("pairingKey").GetString()!, deadline.Token).ConfigureAwait(false);
                if (token is null) { await ErrorAsync(context, 401, "invalid_pairing_key").ConfigureAwait(false); return; }
                RaiseChanged();
                await JsonAsync(context, new { bindingId = id, bearerToken = token, protocolVersion = ProtocolVersion }, deadline.Token).ConfigureAwait(false);
                return;
            }
            var header = context.Request.Headers.Authorization;
            if (header.Count != 1 || header[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.Ordinal))
            { await ErrorAsync(context, 401, "unauthorized").ConfigureAwait(false); return; }
            authorization = await _authorization.AuthorizeAsync(value[7..], deadline.Token).ConfigureAwait(false);
            if (authorization is null) { await ErrorAsync(context, 401, "unauthorized").ConfigureAwait(false); return; }
            if (context.Request.Method == "GET" && path == "/v1/catalog")
            { await JsonAsync(context, new { protocolVersion = ProtocolVersion, commands = _dispatcher.Catalog }, authorization.Token).ConfigureAwait(false); return; }
            if (context.Request.Method != "POST" || path != "/v1/execute")
            { await ErrorAsync(context, 404, "not_found").ConfigureAwait(false); return; }
            using var request = await ReadBodyAsync(context, authorization.Token).ConfigureAwait(false);
            RequireProperties(request.RootElement, "command", "arguments");
            if (request.RootElement.GetProperty("command").ValueKind != JsonValueKind.String ||
                request.RootElement.GetProperty("arguments").ValueKind != JsonValueKind.Object)
                throw new AppAgentCommandException("invalid_request", "Command and object arguments are required.");
            var command = request.RootElement.GetProperty("command").GetString()!;
            if (command.Length > 120 || !_dispatcher.Catalog.Any(entry => string.Equals(entry.Name, command, StringComparison.Ordinal)))
            { await ErrorAsync(context, 400, "unknown_command").ConfigureAwait(false); return; }
            authorization.Token.ThrowIfCancellationRequested();
            var operation = _dispatcher.ExecuteAsync(command, request.RootElement.GetProperty("arguments").Clone(), authorization.Token);
            JsonElement result;
            try { result = await operation.WaitAsync(authorization.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!operation.IsCompleted)
            {
                // Cancellation ends transport immediately, but an uncooperative App task must
                // keep its bounded slot and authorization lifetime until its real work exits.
                releaseRequest = false;
                var retiredAuthorization = authorization;
                authorization = null;
                _ = operation.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    retiredAuthorization.Dispose();
                    _requests.Release();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            authorization.Token.ThrowIfCancellationRequested();
            await JsonAsync(context, new { result }, authorization.Token).ConfigureAwait(false);
        }
        catch (AppAgentCommandException error)
        { await ErrorAsync(context, 400, error.Code).ConfigureAwait(false); }
        catch (ResponseLimitException)
        { await ErrorAsync(context, 413, "response_too_large").ConfigureAwait(false); }
        catch (BadHttpRequestException error)
        { await ErrorAsync(context, error.StatusCode, error.StatusCode == 413 ? "body_too_large" : "invalid_request").ConfigureAwait(false); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { await ErrorAsync(context, 400, "invalid_request").ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!context.RequestAborted.IsCancellationRequested)
                await ErrorAsync(context, deadline.IsCancellationRequested ? 408 : 401,
                    deadline.IsCancellationRequested ? "timeout" : "revoked").ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { ErrorCode = "authorization_store_unavailable"; RaiseChanged(); }
            _logger.LogWarning("A local agent request failed ({Category}).", error.GetType().Name);
            await ErrorAsync(context, 503, "unavailable").ConfigureAwait(false);
        }
        finally
        {
            authorization?.Dispose();
            if (releaseRequest) _requests.Release();
        }
    }

    private bool AdmitPairAttempt()
    {
        lock (_pairingGate)
        {
            var now = _clock.GetUtcNow();
            while (_pairingAttempts.TryPeek(out var first) && now - first >= TimeSpan.FromMinutes(1)) _pairingAttempts.Dequeue();
            if (_pairingAttempts.Count >= 10) return false;
            _pairingAttempts.Enqueue(now); return true;
        }
    }
    private static async Task<JsonDocument> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Headers.ContainsKey("Content-Encoding"))
            throw new AppAgentCommandException("json_required", "Use application/json without content encoding.");
        if (context.Request.ContentLength > MaximumBodyBytes) throw new BadHttpRequestException("Body limit exceeded.", 413);
        using var buffer = new MemoryStream();
        var bytes = new byte[16_384];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumBodyBytes) throw new BadHttpRequestException("Body limit exceeded.", 413);
            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            names.Any(name => !actual.Contains(name, StringComparer.Ordinal))) throw new JsonException();
    }
    private static async Task JsonAsync(HttpContext context, object value, CancellationToken token)
    {
        // Serialize before response headers become irreversible. The stream enforces the
        // wire-size cap and leaves the connection able to return a small controlled error.
        using var buffer = new LimitedResponseStream();
        await JsonSerializer.SerializeAsync(buffer, value, Json, token).ConfigureAwait(false);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = buffer.Length;
        buffer.Position = 0;
        await buffer.CopyToAsync(context.Response.Body, token).ConfigureAwait(false);
    }
    private static async Task ErrorAsync(HttpContext context, int status, string code)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) return;
        if (code.Length > 80 || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')) code = "command_failed";
        context.Response.StatusCode = status;
        try { await JsonAsync(context, new { error = new { code } }, context.RequestAborted).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or IOException) { context.Abort(); }
    }

    private async Task ExpireInvitationsAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ExpiryCheckInterval, token).ConfigureAwait(false);
                await _lifecycle.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (token.IsCancellationRequested) return;
                    if (!await _authorization.ExpirePendingAsync(token).ConfigureAwait(false)) continue;
                    RaiseChanged();
                    if (Bindings.Count == 0) { await StopCoreAsync(CancellationToken.None).ConfigureAwait(false); return; }
                }
                finally { _lifecycle.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ErrorCode = "authorization_store_unavailable";
            _logger.LogWarning("Agent invitation maintenance stopped ({Category}).", error.GetType().Name);
            RaiseChanged();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StopCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var app = _app; _app = null; Endpoint = null;
        var stop = _stop; _stop = null;
        if (stop is not null)
            _ = stop.CancelAsync().ContinueWith(completed => { _ = completed.Exception; stop.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            if (app is not null)
            {
                using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stopDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                try { await app.StopAsync(stopDeadline.Token).ConfigureAwait(false); }
                finally { await app.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            RemoveDiscovery();
            RaiseChanged();
        }
    }
    private void RemoveDiscovery()
    {
        try
        {
            _authorization.VerifyStorageLocation();
            if (File.Exists(DiscoveryFilePath)) File.Delete(DiscoveryFilePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _logger.LogWarning("Agent discovery cleanup deferred."); }
    }

    private sealed class ResponseLimitException : Exception;
    private sealed class LimitedResponseStream : MemoryStream
    {
        private void Check(int count) { if (Length + count > MaximumResponseBytes) throw new ResponseLimitException(); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
    }
    private void RaiseChanged()
    {
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) when (error is not OutOfMemoryException) { _logger.LogWarning("An agent-binding observer failed."); }
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await _authorization.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
}
