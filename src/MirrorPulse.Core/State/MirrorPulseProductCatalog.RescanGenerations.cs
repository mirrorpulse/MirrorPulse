using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private static async Task EnsureRootRescanGenerationSchemaAsync(SqliteConnection connection, long previousVersion, CancellationToken token)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS root_rescan_generations (
                    root_id TEXT PRIMARY KEY REFERENCES deferred_rescan_roots(root_id) ON DELETE CASCADE,
                    generation TEXT NOT NULL UNIQUE,
                    requested_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS full_rescan_request_snapshots (
                    singleton INTEGER PRIMARY KEY REFERENCES full_rescan_checkpoint(singleton) ON DELETE CASCADE CHECK(singleton=1),
                    generation TEXT NOT NULL,
                    captured_utc TEXT NOT NULL,
                    fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
                );
                CREATE TABLE IF NOT EXISTS full_rescan_root_requests (
                    singleton INTEGER NOT NULL REFERENCES full_rescan_request_snapshots(singleton) ON DELETE CASCADE CHECK(singleton=1),
                    root_id TEXT NOT NULL,
                    generation TEXT NOT NULL UNIQUE,
                    requested_utc TEXT NOT NULL,
                    PRIMARY KEY(singleton,root_id)
                );
                """;
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        if (previousVersion < 48)
        {
            var roots = new List<RootId>();
            await using (SqliteCommand missing = connection.CreateCommand())
            {
                missing.Transaction = transaction;
                missing.CommandText = """
                    SELECT r.root_id FROM deferred_rescan_roots r LEFT JOIN root_rescan_generations g USING(root_id)
                    WHERE g.root_id IS NULL ORDER BY r.root_id;
                    """;
                await using var reader = await missing.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) roots.Add(new(ReadRescanGuid(reader.GetString(0))));
            }
            foreach (RootId root in roots)
                await SaveRootRescanRequestCoreAsync(connection, transaction, new(root, Guid.NewGuid(), DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            // These are new administrative obligations, not evidence that the legacy checkpoint
            // scanned them. Recovery must rediscover before capturing or completing any request.
        }
        await ReadRootRescanRequestsCoreAsync(connection, transaction, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async Task SaveRootRescanRequestCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        MirrorPulseRootRescanRequest request, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO deferred_rescan_roots(root_id) VALUES($root) ON CONFLICT(root_id) DO NOTHING;
            INSERT INTO root_rescan_generations(root_id,generation,requested_utc) VALUES($root,$generation,$requested)
            ON CONFLICT(root_id) DO UPDATE SET generation=excluded.generation,requested_utc=excluded.requested_utc;
            """;
        command.Parameters.AddWithValue("$root", request.RootId.ToString());
        command.Parameters.AddWithValue("$generation", request.Generation.ToString("D"));
        command.Parameters.AddWithValue("$requested", request.RequestedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<MirrorPulseRootRescanRequest>> ReadRootRescanRequestsCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.root_id,g.generation,g.requested_utc FROM deferred_rescan_roots r
            LEFT JOIN root_rescan_generations g USING(root_id)
            UNION ALL SELECT NULL,g.generation,g.requested_utc FROM root_rescan_generations g
            LEFT JOIN deferred_rescan_roots r USING(root_id) WHERE r.root_id IS NULL ORDER BY 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var requests = new List<MirrorPulseRootRescanRequest>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                throw new InvalidDataException("The root rescan request has no matching obligation and generation.");
            requests.Add(new(new(ReadRescanGuid(reader.GetString(0))), ReadRescanGuid(reader.GetString(1)), ReadRescanTimestamp(reader.GetString(2))));
        }
        return requests.AsReadOnly();
    }

    private async Task CaptureRootRescanRequestsCoreAsync(Guid generation, SqliteTransaction transaction, CancellationToken token)
    {
        IReadOnlyList<MirrorPulseRootRescanRequest> requests = await ReadRootRescanRequestsCoreAsync(_connection, transaction, token).ConfigureAwait(false);
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        await using (SqliteCommand header = _connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = """
                DELETE FROM full_rescan_request_snapshots WHERE singleton=1;
                INSERT INTO full_rescan_request_snapshots(singleton,generation,captured_utc,fingerprint) VALUES(1,$generation,$captured,$fingerprint);
                """;
            header.Parameters.AddWithValue("$generation", generation.ToString("D"));
            header.Parameters.AddWithValue("$captured", captured.ToString("O", CultureInfo.InvariantCulture));
            header.Parameters.AddWithValue("$fingerprint", RescanSnapshotFingerprint(generation, captured, requests));
            await header.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (MirrorPulseRootRescanRequest request in requests)
        {
            if (request.RequestedAt > captured) throw new InvalidDataException("The root rescan request is later than discovery.");
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO full_rescan_root_requests(singleton,root_id,generation,requested_utc) VALUES(1,$root,$generation,$requested);";
            insert.Parameters.AddWithValue("$root", request.RootId.ToString());
            insert.Parameters.AddWithValue("$generation", request.Generation.ToString("D"));
            insert.Parameters.AddWithValue("$requested", request.RequestedAt.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<MirrorPulseRootRescanRequest>> ReadCapturedRootRescanRequestsCoreAsync(
        Guid generation, SqliteTransaction? transaction, CancellationToken token)
    {
        MirrorPulseFullRescanCheckpoint checkpoint = await ReadFullRescanCoreAsync(token, transaction).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The full rescan generation changed.");
        if (checkpoint.Generation != generation || checkpoint.Phase == MirrorPulseFullRescanPhase.Requested)
            throw new InvalidOperationException("The requested generation has no discovery snapshot.");
        DateTimeOffset captured;
        byte[] fingerprint;
        await using (SqliteCommand header = _connection.CreateCommand())
        {
            header.Transaction = transaction;
            header.CommandText = "SELECT generation,captured_utc,fingerprint FROM full_rescan_request_snapshots WHERE singleton=1;";
            await using var reader = await header.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || ReadRescanGuid(reader.GetString(0)) != generation)
                throw new InvalidDataException("The full rescan has no matching discovery snapshot.");
            captured = ReadRescanTimestamp(reader.GetString(1));
            fingerprint = (byte[])reader.GetValue(2);
        }
        await using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT root_id,generation,requested_utc FROM full_rescan_root_requests WHERE singleton=1 ORDER BY root_id;";
        await using var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var requests = new List<MirrorPulseRootRescanRequest>();
        while (await rows.ReadAsync(token).ConfigureAwait(false))
        {
            var request = new MirrorPulseRootRescanRequest(new(ReadRescanGuid(rows.GetString(0))),
                ReadRescanGuid(rows.GetString(1)), ReadRescanTimestamp(rows.GetString(2)));
            if (request.RequestedAt > captured) throw new InvalidDataException("The captured root request is later than discovery.");
            requests.Add(request);
        }
        if (!CryptographicOperations.FixedTimeEquals(fingerprint, RescanSnapshotFingerprint(generation, captured, requests)))
            throw new InvalidDataException("The full rescan request snapshot changed after discovery.");
        return requests.AsReadOnly();
    }

    private static byte[] RescanSnapshotFingerprint(Guid generation, DateTimeOffset captured, IReadOnlyList<MirrorPulseRootRescanRequest> requests) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Generation = generation, CapturedAt = captured, Requests = requests }));

    private static Guid ReadRescanGuid(string value) =>
        Guid.TryParseExact(value, "D", out Guid parsed) && parsed != Guid.Empty && value == parsed.ToString("D") ? parsed :
            throw new InvalidDataException("The rescan identifier is invalid.");

    private static DateTimeOffset ReadRescanTimestamp(string value) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed) &&
            parsed.Offset == TimeSpan.Zero ? parsed : throw new InvalidDataException("The rescan timestamp is invalid.");
}
