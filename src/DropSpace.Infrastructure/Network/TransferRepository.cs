using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace DropSpace.Infrastructure.Network;

public sealed class TransferRepository(SqliteDatabase database)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task UpsertPeerAsync(PeerDevice peer, string secretKeyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (string.IsNullOrWhiteSpace(secretKeyId)) throw new ArgumentException("A secret key identifier is required.", nameof(secretKeyId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO paired_devices (id, display_name, platform, identity_fingerprint, secret_key_id, capabilities, created_at_utc, last_seen_at_utc, is_blocked)
                VALUES (@id, @name, @platform, @fingerprint, @secret, @capabilities, @created, @last_seen, @blocked)
                ON CONFLICT(id) DO UPDATE SET display_name = excluded.display_name, platform = excluded.platform,
                    identity_fingerprint = excluded.identity_fingerprint, secret_key_id = excluded.secret_key_id,
                    capabilities = excluded.capabilities, last_seen_at_utc = excluded.last_seen_at_utc, is_blocked = excluded.is_blocked;
                """;
            command.Parameters.AddWithValue("@id", peer.Id.ToString("D"));
            command.Parameters.AddWithValue("@name", peer.DisplayName);
            command.Parameters.AddWithValue("@platform", (int)peer.Platform);
            command.Parameters.AddWithValue("@fingerprint", peer.IdentityFingerprint);
            command.Parameters.AddWithValue("@secret", secretKeyId);
            command.Parameters.AddWithValue("@capabilities", (int)peer.Capabilities);
            command.Parameters.AddWithValue("@created", peer.CreatedAtUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("@last_seen", peer.LastSeenAtUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@blocked", peer.TrustState == PeerTrustState.Blocked ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PeerDevice>> GetPeersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, display_name, platform, identity_fingerprint, capabilities, created_at_utc, last_seen_at_utc, is_blocked FROM paired_devices ORDER BY display_name COLLATE NOCASE;";
        var peers = new List<PeerDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.Parse(reader.GetString(0));
            var blocked = reader.GetInt32(7) != 0;
            peers.Add(new PeerDevice(
                id,
                reader.GetString(1),
                (DevicePlatform)reader.GetInt32(2),
                reader.GetString(3),
                (PeerCapability)reader.GetInt32(4),
                blocked ? PeerTrustState.Blocked : PeerTrustState.Trusted,
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return peers;
    }

    public async Task DeletePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM paired_devices WHERE id = @id;";
            command.Parameters.AddWithValue("@id", peerId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task CreateSessionAsync(TransferSession session, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO transfer_sessions (id, direction, mode, peer_id, state, created_at_utc, completed_at_utc, item_count, total_bytes, transferred_bytes, error_category)
                VALUES (@id, @direction, @mode, @peer, @state, @created, @completed, @items, @total, @transferred, @error);
                """;
            AddSessionParameters(command, session);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateSessionAsync(TransferSession session, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE transfer_sessions SET state = @state, completed_at_utc = @completed, transferred_bytes = @transferred, error_category = @error
                WHERE id = @id;
                """;
            command.Parameters.AddWithValue("@id", session.Id.ToString("D"));
            command.Parameters.AddWithValue("@state", (int)session.State);
            command.Parameters.AddWithValue("@completed", session.CompletedAtUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@transferred", session.TransferredBytes);
            command.Parameters.AddWithValue("@error", session.ErrorCategory ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static void AddSessionParameters(SqliteCommand command, TransferSession session)
    {
        command.Parameters.AddWithValue("@id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("@direction", (int)session.Direction);
        command.Parameters.AddWithValue("@mode", (int)session.Mode);
        command.Parameters.AddWithValue("@peer", session.PeerId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@state", (int)session.State);
        command.Parameters.AddWithValue("@created", session.CreatedAtUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("@completed", session.CompletedAtUtc?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@items", session.ItemCount);
        command.Parameters.AddWithValue("@total", session.TotalBytes);
        command.Parameters.AddWithValue("@transferred", session.TransferredBytes);
        command.Parameters.AddWithValue("@error", session.ErrorCategory ?? (object)DBNull.Value);
    }
}
