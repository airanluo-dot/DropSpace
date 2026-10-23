using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using DropSpace.Core.Agent;
using DropSpace.Infrastructure.Agent;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AppAgentHostTests
{
    private string _root = null!;
    private AppStoragePaths _paths = null!;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-agent-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppStoragePaths(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // This fixture's only recursive removal is its freshly generated, fully qualified
        // test root; no user data or paths received from HTTP are cleanup targets.
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DropSpace-agent-tests")) + Path.DirectorySeparatorChar;
        Assert.IsTrue(Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase));
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task Pairing_IsExplicitOneTimeAndPersistsOnlyHashesForTheSameBinding()
    {
        await using var host = CreateHost();
        await host.StartAsync();
        Assert.IsNull(host.Endpoint);
        Assert.IsFalse(Directory.Exists(_paths.Agent));
        var invitation = await host.CreateBindingAsync("Local test agent");
        Assert.AreEqual("127.0.0.1", invitation.Endpoint.Host);
        Assert.AreEqual(43, invitation.PairingKey.Length);
        Assert.AreEqual(AppAgentBindingState.Pending, host.Bindings.Single().State);
        using var client = Client(host);
        using var health = await client.GetAsync("v1/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.IsFalse((await health.Content.ReadAsStringAsync()).Contains("Local test agent", StringComparison.Ordinal));
        using var missingAuth = await client.GetAsync("v1/catalog");
        Assert.AreEqual(HttpStatusCode.Unauthorized, missingAuth.StatusCode);
        var token = await PairAsync(client, invitation);
        Assert.AreEqual(invitation.BindingId, host.Bindings.Single().Id);
        Assert.AreEqual(AppAgentBindingState.Paired, host.Bindings.Single().State);
        using var replay = await client.PostAsJsonAsync("v1/pair", new { bindingId = invitation.BindingId, pairingKey = invitation.PairingKey });
        Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var catalog = await client.GetAsync("v1/catalog");
        Assert.AreEqual(HttpStatusCode.OK, catalog.StatusCode);
        using var executed = await client.PostAsJsonAsync("v1/execute", new { command = "test.echo", arguments = new { text = "资料" } });
        Assert.AreEqual(HttpStatusCode.OK, executed.StatusCode);
        using var result = JsonDocument.Parse(await executed.Content.ReadAsStringAsync());
        Assert.AreEqual("资料", result.RootElement.GetProperty("result").GetProperty("text").GetString());
        var disk = await File.ReadAllTextAsync(Path.Combine(_paths.Agent, "bindings.json"));
        Assert.IsFalse(disk.Contains(invitation.PairingKey, StringComparison.Ordinal));
        Assert.IsFalse(disk.Contains(token, StringComparison.Ordinal));
        StringAssert.Contains(disk, "bearerHash");
        using var discovery = JsonDocument.Parse(await File.ReadAllTextAsync(host.DiscoveryFilePath));
        Assert.AreEqual(host.Endpoint!.AbsoluteUri, discovery.RootElement.GetProperty("endpoint").GetString());
        Assert.IsFalse((await File.ReadAllTextAsync(host.DiscoveryFilePath)).Contains("Hash", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Revocation_CancelsInflightAndRejectsEveryLaterRequestWithoutRevokingOthers()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new FakeDispatcher(async (_, arguments, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return arguments; });
        await using var host = CreateHost(dispatcher);
        var first = await host.CreateBindingAsync("Revoked agent");
        var second = await host.CreateBindingAsync("Other agent");
        using var client = Client(host);
        var token = await PairAsync(client, first);
        var other = await PairAsync(client, second);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var running = client.PostAsJsonAsync("v1/execute", new { command = "test.echo", arguments = new { } });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(await host.RevokeAsync(first.BindingId));
        using var cancelled = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(HttpStatusCode.Unauthorized, cancelled.StatusCode);
        using var retry = await client.GetAsync("v1/catalog");
        Assert.AreEqual(HttpStatusCode.Unauthorized, retry.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other);
        using var allowed = await client.GetAsync("v1/catalog");
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        Assert.AreEqual(second.BindingId, host.Bindings.Single().Id);
        await host.RevokeAsync(second.BindingId);
        Assert.IsNull(host.Endpoint);
        Assert.IsFalse(File.Exists(host.DiscoveryFilePath));
    }

    [TestMethod]
    public async Task Restart_RestoresBearerButNeverResurrectsDeletedBindings()
    {
        string token;
        Guid id;
        await using (var host = CreateHost())
        {
            var invitation = await host.CreateBindingAsync("Persistent agent"); id = invitation.BindingId;
            using var client = Client(host);
            token = await PairAsync(client, invitation);
        }
        await using (var restored = CreateHost())
        {
            await restored.StartAsync();
            Assert.IsNotNull(restored.Endpoint);
            Assert.AreEqual(id, restored.Bindings.Single().Id);
            using var client = Client(restored);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.GetAsync("v1/catalog");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            await restored.RevokeAsync(id);
        }
        await using var empty = CreateHost();
        await empty.StartAsync();
        Assert.IsNull(empty.Endpoint);
        Assert.AreEqual(0, empty.Bindings.Count);
    }

    [TestMethod]
    public async Task ExpiredPairingAndCorruptStoresFailClosedWithoutReplacingAuthorizationData()
    {
        var clock = new ManualClock();
        AppAgentPairingInvitation invitation;
        await using (var host = CreateHost(clock: clock)) invitation = await host.CreateBindingAsync("Expired pending agent");
        clock.Advance(TimeSpan.FromMinutes(31));
        await using (var restarted = CreateHost(clock: clock))
        {
            await restarted.StartAsync();
            Assert.IsNull(restarted.Endpoint);
            await restarted.CreateBindingAsync("Fresh pending agent");
            using var client = Client(restarted);
            using var expired = await client.PostAsJsonAsync("v1/pair", new { bindingId = invitation.BindingId, pairingKey = invitation.PairingKey });
            Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
        }
        var path = Path.Combine(_paths.Agent, "bindings.json");
        const string malformed = "{\"version\":1,\"grants\":[null]}";
        await File.WriteAllTextAsync(path, malformed);
        await using var broken = CreateHost();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => broken.StartAsync());
        Assert.IsNull(broken.Endpoint);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => broken.CreateBindingAsync("Cannot overwrite corruption"));
        Assert.AreEqual(malformed, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task HttpBoundary_RejectsBrowserOriginsHostSpoofingOversizeAndUncataloguedCommands()
    {
        await using var host = CreateHost();
        var invitation = await host.CreateBindingAsync("Boundary test");
        using var client = Client(host);
        var token = await PairAsync(client, invitation);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (header, value) in new[] { ("Origin", "https://example.com"), ("Origin", "null"),
            ("Referer", "http://127.0.0.1/"), ("Sec-Fetch-Site", "none"), ("Host", $"localhost:{host.Endpoint!.Port}") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/catalog");
            request.Headers.TryAddWithoutValidation(header, value);
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, header);
        }
        using var form = await client.PostAsync("v1/execute", new FormUrlEncodedContent(new Dictionary<string, string> { ["command"] = "test.echo" }));
        Assert.AreEqual(HttpStatusCode.BadRequest, form.StatusCode);
        using var duplicate = await client.PostAsync("v1/execute", new StringContent(
            "{\"command\":\"test.echo\",\"command\":\"test.echo\",\"arguments\":{}}", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var unknown = await client.PostAsJsonAsync("v1/execute", new { command = "System.Reflection", arguments = new { } });
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var oversized = await client.PostAsync("v1/execute", new StringContent(new string(' ', AppAgentHost.MaximumBodyBytes + 1), Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var stillAlive = await client.GetAsync("v1/health");
        Assert.AreEqual(HttpStatusCode.OK, stillAlive.StatusCode);
    }

    [TestMethod]
    public async Task PairingAttempts_AreRateLimitedAndRecoverAfterTheBoundedWindow()
    {
        var clock = new ManualClock();
        await using var host = CreateHost(clock: clock);
        var invitation = await host.CreateBindingAsync("Rate-limit test");
        using var client = Client(host);
        for (var count = 0; count < 10; count++)
        {
            using var wrong = await client.PostAsJsonAsync("v1/pair", new { bindingId = invitation.BindingId, pairingKey = new string('A', 43) });
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        using var limited = await client.PostAsJsonAsync("v1/pair", new { bindingId = invitation.BindingId, pairingKey = invitation.PairingKey });
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(80, (await PairAsync(client, invitation)).Length);
    }

    [TestMethod]
    public async Task Timeout_HoldsBoundedExecutionSlotsUntilActualApplicationWorkSettles()
    {
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new FakeDispatcher((_, _, _) => release.Task);
        await using var host = CreateHost(dispatcher, timeout: TimeSpan.FromMilliseconds(250));
        var invitation = await host.CreateBindingAsync("Timeout test");
        using var client = Client(host);
        var token = await PairAsync(client, invitation);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        for (var count = 0; count < 4; count++)
        {
            using var response = await client.PostAsJsonAsync("v1/execute", new { command = "test.echo", arguments = new { } });
            Assert.AreEqual(HttpStatusCode.RequestTimeout, response.StatusCode);
        }
        using (var busy = await client.GetAsync("v1/catalog")) Assert.AreEqual(HttpStatusCode.TooManyRequests, busy.StatusCode);
        release.SetResult(Empty);
        await Task.Delay(100);
        using var recovered = await client.GetAsync("v1/catalog");
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
    }

    [TestMethod]
    public async Task RevocationJournal_DeniesAfterSnapshotWriteFailureAndRestart()
    {
        string revokedToken, otherToken;
        Guid revokedId;
        await using (var host = CreateHost())
        {
            var revoked = await host.CreateBindingAsync("Retired despite locked snapshot");
            var other = await host.CreateBindingAsync("Survives restart");
            revokedId = revoked.BindingId;
            using var client = Client(host);
            revokedToken = await PairAsync(client, revoked);
            otherToken = await PairAsync(client, other);
            using (var locked = new FileStream(Path.Combine(_paths.Agent, "bindings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => host.RevokeAsync(revokedId));
                Assert.AreEqual("authorization_store_unavailable", host.ErrorCode);
                Assert.IsFalse(host.Bindings.Any(binding => binding.Id == revokedId));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", revokedToken);
                using var rejected = await client.GetAsync("v1/catalog");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
                StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(_paths.Agent, "revoked-bindings.json")), revokedId.ToString());
            }
        }
        await using var restored = CreateHost();
        await restored.StartAsync();
        Assert.IsNotNull(restored.Endpoint);
        Assert.IsFalse(restored.Bindings.Any(binding => binding.Id == revokedId));
        using var restarted = Client(restored);
        restarted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", revokedToken);
        using var refused = await restarted.GetAsync("v1/catalog");
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
        restarted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        using var allowed = await restarted.GetAsync("v1/catalog");
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
    }

    [TestMethod]
    public async Task PendingExpiry_StopsListenerRemovesDiscoveryAndUsesPrivateStorage()
    {
        var clock = new ManualClock();
        await using var host = CreateHost(clock: clock);
        await host.CreateBindingAsync("Expires while running");
        Assert.IsTrue(File.Exists(host.DiscoveryFilePath));
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectoryInfo(_paths.Agent).GetAccessControl();
            Assert.IsTrue(acl.AreAccessRulesProtected);
            using var identity = WindowsIdentity.GetCurrent();
            var allowed = new[] { identity.User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Assert.IsTrue(allowed.Contains(rule.IdentityReference.Value), rule.IdentityReference.Value);
        }
        clock.Advance(TimeSpan.FromMinutes(31));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.Endpoint is not null || File.Exists(host.DiscoveryFilePath)) await Task.Delay(20, deadline.Token);
        Assert.AreEqual(0, host.Bindings.Count);
        await host.CreateBindingAsync("New user invitation restarts listening");
        using var client = Client(host);
        using var health = await client.GetAsync("v1/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
    }

    [TestMethod]
    public async Task ResponseLimit_ReturnsControlledErrorAndKeepsHostAvailable()
    {
        var tooLarge = JsonSerializer.SerializeToElement(new { text = new string('x', AppAgentHost.MaximumResponseBytes) });
        var dispatcher = new FakeDispatcher((_, _, _) => Task.FromResult(tooLarge));
        await using var host = CreateHost(dispatcher);
        var invitation = await host.CreateBindingAsync("Response limit");
        using var client = Client(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await PairAsync(client, invitation));
        using var response = await client.PostAsJsonAsync("v1/execute", new { command = "test.echo", arguments = new { } });
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "response_too_large");
        using var health = await client.GetAsync("v1/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
    }

    [TestMethod]
    public async Task LinkedDiscovery_FailsClosedWithoutChangingItsTarget()
    {
        await using (var created = CreateHost()) await created.CreateBindingAsync("Existing pending binding");
        var target = Path.Combine(_root, "unrelated-marker.json");
        const string marker = "This file is outside the Agent storage directory.";
        await File.WriteAllTextAsync(target, marker);
        var discovery = Path.Combine(_paths.Agent, "endpoint.json");
        File.CreateSymbolicLink(discovery, target);
        await using var restarted = CreateHost();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => restarted.StartAsync());
        Assert.IsNull(restarted.Endpoint);
        Assert.AreEqual("authorization_store_unavailable", restarted.ErrorCode);
        Assert.AreEqual(marker, await File.ReadAllTextAsync(target));
    }

    private AppAgentHost CreateHost(IAppAgentDispatcher? dispatcher = null, TimeProvider? clock = null, TimeSpan? timeout = null) =>
        new(_paths, dispatcher ?? new FakeDispatcher((_, arguments, _) => Task.FromResult(arguments)), NullLogger<AppAgentHost>.Instance, clock)
        { RequestTimeout = timeout ?? TimeSpan.FromSeconds(30), ExpiryCheckInterval = TimeSpan.FromMilliseconds(50) };
    private static HttpClient Client(AppAgentHost host) => new(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = host.Endpoint, Timeout = TimeSpan.FromSeconds(10) };
    private static async Task<string> PairAsync(HttpClient client, AppAgentPairingInvitation invitation)
    {
        using var response = await client.PostAsJsonAsync("v1/pair", new { bindingId = invitation.BindingId, pairingKey = invitation.PairingKey });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(invitation.BindingId, body.RootElement.GetProperty("bindingId").GetGuid());
        return body.RootElement.GetProperty("bearerToken").GetString()!;
    }
    private sealed class FakeDispatcher(Func<string, JsonElement, CancellationToken, Task<JsonElement>> execute) : IAppAgentDispatcher
    {
        public IReadOnlyList<AppAgentCommand> Catalog { get; } = [new("test.echo", "Echo approved test arguments.",
            JsonSerializer.SerializeToElement(new { type = "object" }))];
        public Task<JsonElement> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken = default) =>
            execute(command, arguments, cancellationToken);
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
