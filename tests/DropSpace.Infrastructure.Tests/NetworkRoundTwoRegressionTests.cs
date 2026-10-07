using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class NetworkRoundTwoRegressionTests
{
    [TestMethod]
    [DataRow("/V1/PAIRING/HELLO", true)]
    [DataRow("/v1/pairing/hello/", true)]
    [DataRow("/V1/CLIPBOARD", false)]
    [DataRow("/v1/clipboard/", false)]
    public async Task RouterAliasesKeepPrebindingLimitsAndAuthentication(string path, bool pairing)
    {
        await using var fixture = new HostFixture();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        fixture.Host.ConfigureAuthenticationPipeline(app);
        var reached = false;
        app.MapPost(pairing ? DropLinkProtocolRoutes.PairingHello : DropLinkProtocolRoutes.Clipboard,
            (System.Text.Json.JsonElement body) => { reached = true; return Results.NoContent(); });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var body = pairing ? System.Text.Json.JsonSerializer.Serialize(new { value = new string('x', 65_536) }) : "{";
        using var response = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.AreEqual(pairing ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsFalse(reached);
    }

    [TestMethod]
    public async Task PrematureCompletionDoesNotPoisonCompletionAfterReceiverApproval()
    {
        await using var fixture = new HostFixture();
        var receive = await fixture.SeedAsync();
        Assert.AreEqual(TransferSessionState.AwaitingApproval, (await fixture.CompleteAsync(receive)).State);
        Assert.IsTrue(await fixture.Host.ApproveIncomingTransferAsync(fixture.SessionId, true));
        Assert.AreEqual(TransferSessionState.Completed, (await fixture.CompleteAsync(receive)).State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "empty.txt")));
    }

    [TestMethod]
    public async Task FailedCompletionPersistencePreservesPublishedFilesAndReportsFailure()
    {
        await using var fixture = new HostFixture();
        var receive = await fixture.SeedAsync();
        Assert.IsTrue(await fixture.Host.ApproveIncomingTransferAsync(fixture.SessionId, true));
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_completion BEFORE UPDATE OF state ON transfer_sessions WHEN NEW.state = 6 BEGIN SELECT RAISE(ABORT, 'injected completion failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        var result = await fixture.CompleteAsync(receive);
        Assert.AreEqual(TransferSessionState.Failed, result.State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "empty.txt")));
        Assert.AreEqual("empty.txt", result.CompletedRelativePaths.Single());
    }

    [TestMethod]
    public async Task RetiredReceiveKeepsOwnedAndQueuedManagedGatesUsable()
    {
        await using var fixture = new HostFixture();
        var receive = await fixture.SeedAsync();
        var gate = (SemaphoreSlim)receive.GetType().GetProperty("MutationGate")!.GetValue(receive)!;
        await gate.WaitAsync();
        var waiting = gate.WaitAsync();
        try
        {
            ((IDisposable)receive).Dispose();
            gate.Release();
            await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            gate.Release();
        }
        finally { fixture.DetachReceive(); }
    }

    [TestMethod]
    public async Task CallerDisposedDiscoveryRegistrationCanBeDisposedByItsService()
    {
        var descriptor = new DeviceDescriptor(DropLinkProtocolVersion.V1, Guid.NewGuid(), "fixture", DevicePlatform.Windows,
            PeerCapability.None, new string('a', 64), new Uri("https://127.0.0.1:12345"));
        var type = typeof(WindowsDnsSdDiscoveryService).GetNestedType("DnsRegistration", BindingFlags.NonPublic)!;
        using var socket = new System.Net.Sockets.UdpClient();
        var registration = (IAsyncDisposable)Activator.CreateInstance(type,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [socket, descriptor, "fixture"], null)!;
        var service = new WindowsDnsSdDiscoveryService();
        typeof(WindowsDnsSdDiscoveryService).GetField("_registration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, registration);
        await registration.DisposeAsync();
        await service.DisposeAsync();
        await service.DisposeAsync();
    }

    [TestMethod]
    public async Task CompletedRetirementDoesNotRetainItsSessionTask()
    {
        await using var fixture = new HostFixture();
        await fixture.SeedAsync();
        await fixture.Host.StopAsync();
        var tasks = typeof(DropLinkHost).GetField("_sessionRetirementTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Host)!;
        Assert.AreEqual(0, (int)tasks.GetType().GetProperty("Count")!.GetValue(tasks)!);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RejectionRecordsItsTerminalTime(bool remote)
    {
        await using var fixture = new HostFixture();
        var receive = await fixture.SeedAsync();
        if (remote)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            app.Use((context, next) =>
            {
                context.Items[DropLinkAuthenticationMiddleware.AuthenticatedPeerContextKey] =
                    HostFixture.SessionOf(receive).PeerId;
                return next(context);
            });
            fixture.Host.MapRoutes(app);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.PostAsJsonAsync(
                DropLinkProtocolRoutes.TransferAccept(fixture.SessionId), new TransferAcceptRequest(false));
            response.EnsureSuccessStatusCode();
        }
        else
        {
            Assert.IsTrue(await fixture.Host.ApproveIncomingTransferAsync(fixture.SessionId, false));
        }

        var session = HostFixture.SessionOf(receive);
        Assert.AreEqual(TransferSessionState.Rejected, session.State);
        Assert.IsNotNull(session.CompletedAtUtc, "Rejected transfers need a fixed retention origin.");
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed_at_utc FROM transfer_sessions WHERE id = @id;";
        command.Parameters.AddWithValue("@id", fixture.SessionId.ToString("D"));
        Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(), "The terminal time must also be persisted.");
    }

    [TestMethod]
    public async Task PollingLegacyRejectedSessionDoesNotPreventStagingRetirement()
    {
        await using var fixture = new HostFixture();
        var receive = await fixture.SeedAsync();
        Assert.IsTrue(await fixture.Host.ApproveIncomingTransferAsync(fixture.SessionId, false));
        var type = receive.GetType();
        type.GetProperty("Session")!.SetValue(receive, HostFixture.SessionOf(receive) with { CompletedAtUtc = null });
        type.GetProperty("LastActivityUtc")!.SetValue(receive, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(3));
        var root = (string)type.GetProperty("StagingRoot")!.GetValue(receive)!;
        Assert.IsTrue(Directory.Exists(root));

        var snapshot = (Task<TransferStatusResponse>)typeof(DropLinkHost)
            .GetMethod("SnapshotAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [receive, CancellationToken.None])!;
        Assert.AreEqual(TransferSessionState.Rejected, (await snapshot).State);
        await (Task)typeof(DropLinkHost).GetMethod("SweepSessionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Host, [CancellationToken.None])!;

        var sessions = typeof(DropLinkHost).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Host)!;
        Assert.AreEqual(0, (int)sessions.GetType().GetProperty("Count")!.GetValue(sessions)!, "The expired session must leave admission capacity.");
        Assert.IsFalse(Directory.Exists(root), "Polling must not retain expired rejected staging.");
    }

    [TestMethod]
    public async Task InactiveDiscoveryRegistrationIsNotReturnedForAnotherDescriptor()
    {
        var descriptor = new DeviceDescriptor(DropLinkProtocolVersion.V1, Guid.NewGuid(), "fixture", DevicePlatform.Windows,
            PeerCapability.None, new string('a', 64), new Uri("https://127.0.0.1:12345"));
        var type = typeof(WindowsDnsSdDiscoveryService).GetNestedType("DnsRegistration", BindingFlags.NonPublic)!;
        using var socket = new System.Net.Sockets.UdpClient();
        var registration = (IAsyncDisposable)Activator.CreateInstance(type,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [socket, descriptor, "fixture"], null)!;
        var service = new WindowsDnsSdDiscoveryService();
        typeof(WindowsDnsSdDiscoveryService).GetField("_registration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, registration);
        // A fresh descriptor must be validated instead of returning the inactive handle.
        try { await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.RegisterAsync(descriptor)); }
        finally { await service.DisposeAsync(); }
    }

    [TestMethod]
    public async Task ReceiveAssemblyPreservesPreviouslyCommittedTemporaryLookingFilename()
    {
        await using var fixture = new HostFixture();
        var first = "report.txt." + fixture.SessionId.ToString("N") + ".tmp";
        var receive = await fixture.SeedAsync(first, "report.txt");
        Assert.IsTrue(await fixture.Host.ApproveIncomingTransferAsync(fixture.SessionId, true));
        var result = await fixture.CompleteAsync(receive);
        Assert.AreEqual(TransferSessionState.Completed, result.State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, first)));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "report.txt")));
        Assert.AreEqual(2, result.CompletedRelativePaths.Count);
    }

    [TestMethod]
    public async Task ReceiveAssemblyDoesNotTruncatePreexistingTemporaryLookingFile()
    {
        await using var fixture = new HostFixture();
        var receive = await fixture.SeedAsync();
        Directory.CreateDirectory(fixture.Destination);
        var existing = Path.Combine(fixture.Destination, "empty.txt." + fixture.SessionId.ToString("N") + ".tmp");
        await File.WriteAllTextAsync(existing, "existing user content");
        Assert.IsTrue(await fixture.Host.ApproveIncomingTransferAsync(fixture.SessionId, true));
        Assert.AreEqual(TransferSessionState.Completed, (await fixture.CompleteAsync(receive)).State);
        Assert.AreEqual("existing user content", await File.ReadAllTextAsync(existing));
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly DropLinkPairingService _pairing;
        public AppStoragePaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "DropSpace-network-round-two", Guid.NewGuid().ToString("N")));
        public Guid SessionId { get; } = Guid.NewGuid();
        public string Destination => Path.Combine(Paths.Root, "received");
        public SqliteDatabase Database { get; }
        public TransferRepository Repository { get; }
        public DropLinkHost Host { get; }
        public StagingLeaseStore Leases { get; }
        public HostFixture()
        {
            var identities = new DeviceIdentityStore(Paths);
            var secrets = new DeviceSecretStore(Paths);
            _pairing = new(identities, secrets);
            Database = new(Paths, NullLogger<SqliteDatabase>.Instance);
            Repository = new(Database);
            Leases = new(Paths, NullLogger<StagingLeaseStore>.Instance);
            Host = new(identities, secrets, _pairing, Repository, NullLogger<DropLinkHost>.Instance, new(), Leases);
        }
        public async Task<object> SeedAsync(params string[] relativePaths)
        {
            if (relativePaths.Length == 0) relativePaths = ["empty.txt"];
            var session = new TransferSession(SessionId, TransferDirection.Receive, TransferMode.Handoff, Guid.NewGuid(),
                TransferSessionState.AwaitingApproval, DateTimeOffset.UtcNow, null, relativePaths.Length, 0, 0, null);
            var manifest = TransferManifestPolicy.Create(SessionId,
                relativePaths.Select(path => new TransferItemManifest(Guid.NewGuid(), TransferItemKind.File, path, path, 0, Convert.ToHexString(SHA256.HashData([])), "text/plain", 0)).ToArray());
            await Repository.CreateSessionAsync(session);
            var lease = await Leases.AcquireAsync("droplink-receive", "transfers/fixture", sensitivePlaintext: false);
            var type = typeof(DropLinkHost).GetNestedType("ReceiveTransfer", BindingFlags.NonPublic)!;
            var receive = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, [session, manifest, lease.RootPath, Destination, lease], null)!;
            var sessions = typeof(DropLinkHost).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
            Assert.IsTrue((bool)sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, [SessionId, receive])!);
            return receive;
        }
        public Task<TransferCompleteResponse> CompleteAsync(object receive) =>
            (Task<TransferCompleteResponse>)typeof(DropLinkHost).GetMethod("GetOrStartFinalizationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Host, [receive])!;
        public static TransferSession SessionOf(object receive) =>
            (TransferSession)receive.GetType().GetProperty("Session")!.GetValue(receive)!;
        public void DetachReceive()
        {
            var sessions = typeof(DropLinkHost).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
            var remove = sessions.GetType().GetMethods().Single(method => method.Name == "TryRemove" && method.GetParameters().Length == 2);
            remove.Invoke(sessions, [SessionId, null]);
        }
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await _pairing.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true);
        }
    }
}
