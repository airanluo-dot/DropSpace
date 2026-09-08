using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace DropSpace.Infrastructure.Network;

public sealed class TransferRepository(SqliteDatabase database)
{

    public async Task UpsertPeerAsync(PeerDevice peer, string secretKeyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (string.IsNullOrWhiteSpace(secretKeyId)) throw new ArgumentException("A secret key identifier is required.", nameof(secretKeyId));
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO paired_devices (id, display_name, platform, identity_fingerprint, secret_key_id, capabilities, created_at_utc, last_seen_at_utc, is_blocked, trust_state)
                VALUES (@id, @name, @platform, @fingerprint, @secret, @capabilities, @created, @last_seen, @blocked, @trust_state)
                ON CONFLICT(id) DO UPDATE SET display_name = excluded.display_name, platform = excluded.platform,
                    identity_fingerprint = excluded.identity_fingerprint, secret_key_id = excluded.secret_key_id,
                    capabilities = excluded.capabilities, last_seen_at_utc = excluded.last_seen_at_utc,
                    is_blocked = excluded.is_blocked, trust_state = excluded.trust_state;
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
            command.Parameters.AddWithValue("@trust_state", (int)peer.TrustState);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { database.WriteGate.Release(); }
    }

    public async Task<IReadOnlyList<PeerDevice>> GetPeersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, display_name, platform, identity_fingerprint, capabilities, created_at_utc, last_seen_at_utc, is_blocked, trust_state FROM paired_devices ORDER BY display_name COLLATE NOCASE;";
        var peers = new List<PeerDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.Parse(reader.GetString(0));
            var blocked = reader.GetInt32(7) != 0;
            var persistedState = (PeerTrustState)reader.GetInt32(8);
            var trustState = Enum.IsDefined(persistedState)
                ? persistedState
                : blocked ? PeerTrustState.Blocked : PeerTrustState.Unknown;
            peers.Add(new PeerDevice(
                id,
                reader.GetString(1),
                (DevicePlatform)reader.GetInt32(2),
                reader.GetString(3),
                (PeerCapability)reader.GetInt32(4),
                blocked ? PeerTrustState.Blocked : trustState,
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return peers;
    }

    public async Task<PeerTrustState?> GetPeerTrustStateAsync(
        Guid peerId,
        CancellationToken cancellationToken = default)
    {
        if (peerId == Guid.Empty) return null;
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_blocked, trust_state FROM paired_devices WHERE id = @id;";
        command.Parameters.AddWithValue("@id", peerId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        if (reader.GetInt32(0) != 0) return PeerTrustState.Blocked;
        var persistedState = (PeerTrustState)reader.GetInt32(1);
        return Enum.IsDefined(persistedState) ? persistedState : PeerTrustState.Unknown;
    }

    public async Task EnsurePairingPendingAsync(
        DeviceDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.DeviceId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(descriptor));
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var stateCommand = connection.CreateCommand())
            {
                stateCommand.Transaction = (SqliteTransaction)transaction;
                stateCommand.CommandText = "SELECT is_blocked, trust_state FROM paired_devices WHERE id = @id;";
                stateCommand.Parameters.AddWithValue("@id", descriptor.DeviceId.ToString("D"));
                var hasPersistedState = false;
                var isBlocked = false;
                var state = PeerTrustState.Unknown;
                await using (var stateReader = await stateCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await stateReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        hasPersistedState = true;
                        isBlocked = stateReader.GetInt32(0) != 0;
                        state = (PeerTrustState)stateReader.GetInt32(1);
                    }
                }

                if (hasPersistedState)
                {
                    if (isBlocked || state == PeerTrustState.Blocked)
                    {
                        throw new UnauthorizedAccessException("The peer is blocked and cannot be paired.");
                    }

                    // A re-pair must not temporarily de-authorize an already trusted peer.
                    // Its existing secret remains valid until the new secret and metadata are
                    // committed by the normal pairing completion path.
                    if (state == PeerTrustState.Trusted)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                }
            }

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO paired_devices (
                    id, display_name, platform, identity_fingerprint, secret_key_id,
                    capabilities, created_at_utc, last_seen_at_utc, is_blocked, trust_state)
                VALUES (@id, @name, @platform, @fingerprint, @secret, @capabilities, @created, NULL, 0, @state)
                ON CONFLICT(id) DO UPDATE SET
                    display_name = excluded.display_name,
                    platform = excluded.platform,
                    identity_fingerprint = excluded.identity_fingerprint,
                    capabilities = excluded.capabilities,
                    is_blocked = 0,
                    trust_state = excluded.trust_state;
                """;
            command.Parameters.AddWithValue("@id", descriptor.DeviceId.ToString("D"));
            command.Parameters.AddWithValue("@name", descriptor.DisplayName);
            command.Parameters.AddWithValue("@platform", (int)descriptor.Platform);
            command.Parameters.AddWithValue("@fingerprint", descriptor.IdentityFingerprint);
            command.Parameters.AddWithValue("@secret", descriptor.DeviceId.ToString("N"));
            command.Parameters.AddWithValue("@capabilities", (int)descriptor.Capabilities);
            command.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("@state", (int)PeerTrustState.PairingPending);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { database.WriteGate.Release(); }
    }

    public async Task UpdatePeerTrustStateAsync(
        Guid peerId,
        PeerTrustState state,
        CancellationToken cancellationToken = default)
    {
        if (peerId == Guid.Empty || !Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE paired_devices SET trust_state = @state, is_blocked = @blocked WHERE id = @id;";
            command.Parameters.AddWithValue("@state", (int)state);
            command.Parameters.AddWithValue("@blocked", state == PeerTrustState.Blocked ? 1 : 0);
            command.Parameters.AddWithValue("@id", peerId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { database.WriteGate.Release(); }
    }

    public async Task DeletePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM paired_devices WHERE id = @id;";
            command.Parameters.AddWithValue("@id", peerId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { database.WriteGate.Release(); }
    }

    public async Task CreateSessionAsync(TransferSession session, CancellationToken cancellationToken = default)
    {
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        finally { database.WriteGate.Release(); }
    }

    public async Task UpdateSessionAsync(TransferSession session, CancellationToken cancellationToken = default)
    {
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        finally { database.WriteGate.Release(); }
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
