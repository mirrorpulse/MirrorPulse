using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed record MirrorPulseWorkerRequestRecord(
    Guid OperationId,
    InstanceId InstanceId,
    byte[] Fingerprint,
    int Attempt,
    DateTimeOffset? NextAttemptAt,
    byte[]? StableFingerprint = null);

public sealed record MirrorPulseUserCommandRecord(
    Guid CommandId,
    string Action,
    string TargetId,
    string State);

public sealed record MirrorPulseTransferProgress(
    string Operation,
    long BytesTransferred,
    long? TotalBytes,
    DateTimeOffset UpdatedAt);

public sealed record MirrorPulseInstanceRuntimeState(
    InstanceId InstanceId,
    string Phase,
    bool RequiresFullRescan,
    DateTimeOffset? LastSuccessfulSync,
    string? LastErrorCode = null,
    MirrorPulseTransferProgress? TransferProgress = null);

/// <summary>
/// MP's separate product catalog. CfSharp owns Cloud Files journal, batch, conflict and checkpoint
/// tables in its own database; this catalog tracks Worker idempotency, user intent, upload
/// conflicts, and non-authoritative CfSharp remote-conflict UI projections.
/// </summary>
public sealed partial class MirrorPulseProductCatalog : IAsyncDisposable
{
    private readonly FileStream _owner;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal SemaphoreSlim CredentialGate { get; } = new(1, 1);
    private bool _disposed;

    private MirrorPulseProductCatalog(FileStream owner, SqliteConnection connection)
    {
        _owner = owner;
        _connection = connection;
    }

    public static async Task<MirrorPulseProductCatalog> OpenAsync(
        MirrorPulseStoragePaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string path = paths.ProductCatalogDatabasePath;
        if (string.Equals(path, paths.CfSharpStateDatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The MP product catalog must be separate from CfSharp state.");
        }

        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The product catalog has no parent directory.");
        Directory.CreateDirectory(directory);
        FileStream owner = new(
            path + ".owner",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.DeleteOnClose);
        SqliteConnection? connection = null;
        try
        {
            var settings = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true,
            };
            connection = new SqliteConnection(settings.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (SqliteCommand mode = connection.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode=WAL;";
                object? result = await mode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(result?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The MP product catalog could not enable SQLite WAL mode.");
                }
            }

            await using (SqliteCommand version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version;";
                long currentVersion = (long)(await version.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false) ?? 0L);
                if (currentVersion > 19)
                {
                    throw new InvalidDataException("The MP product catalog schema is newer than this Host supports.");
                }
            }

            await using (SqliteCommand schema = connection.CreateCommand())
            {
                schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS full_rescan_checkpoint (
                        singleton INTEGER PRIMARY KEY CHECK (singleton=1),
                        generation TEXT NOT NULL,
                        phase INTEGER NOT NULL,
                        updated_utc TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS deferred_rescan_roots (root_id TEXT PRIMARY KEY);
                    CREATE TABLE IF NOT EXISTS worker_requests (
                        operation_id TEXT PRIMARY KEY,
                        instance_id TEXT NOT NULL,
                        fingerprint BLOB NOT NULL,
                        stable_fingerprint BLOB NULL,
                        attempt INTEGER NOT NULL DEFAULT 0,
                        next_attempt_utc TEXT NULL
                    );
                    CREATE TABLE IF NOT EXISTS pending_remote_batches (
                        instance_id TEXT PRIMARY KEY,
                        batch_id TEXT NOT NULL,
                        payload BLOB NOT NULL,
                        candidate_snapshot BLOB NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS blocked_local_operations (
                        operation_id TEXT PRIMARY KEY,
                        instance_id TEXT NULL,
                        relative_path TEXT NOT NULL,
                        reason INTEGER NOT NULL,
                        observed_utc TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS mutation_intents (
                        operation_id TEXT PRIMARY KEY,
                        payload BLOB NOT NULL,
                        state INTEGER NOT NULL,
                        accepted_revision TEXT NULL,
                        updated_utc TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS content_acceptance_proofs (
                        operation_id TEXT PRIMARY KEY REFERENCES mutation_intents(operation_id),
                        payload BLOB NOT NULL,
                        receipt BLOB NULL
                    );
                    CREATE TABLE IF NOT EXISTS user_commands (
                        command_id TEXT PRIMARY KEY,
                        action TEXT NOT NULL,
                        target_id TEXT NOT NULL,
                        state TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS remote_conflict_projections (
                        conflict_id TEXT PRIMARY KEY,
                        instance_id TEXT NOT NULL,
                        change_id TEXT NOT NULL,
                        relative_path TEXT NOT NULL,
                        reason INTEGER NOT NULL,
                        version_comparison INTEGER NOT NULL,
                        local_revision TEXT NULL,
                        remote_revision TEXT NULL,
                        detected_utc TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS upload_conflicts (
                        conflict_id TEXT PRIMARY KEY,
                        instance_id TEXT NOT NULL,
                        change_id TEXT NOT NULL,
                        relative_path TEXT NOT NULL,
                        reason INTEGER NOT NULL,
                        version_comparison INTEGER NOT NULL,
                        local_revision TEXT NULL,
                        remote_revision TEXT NULL,
                        detected_utc TEXT NOT NULL,
                        status INTEGER NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS instance_runtime (
                        instance_id TEXT PRIMARY KEY,
                        phase TEXT NOT NULL,
                        requires_full_rescan INTEGER NOT NULL,
                        last_successful_sync_utc TEXT NULL,
                        last_error_code TEXT NULL,
                        transfer_operation TEXT NULL,
                        transfer_bytes INTEGER NULL,
                        transfer_total INTEGER NULL,
                        transfer_updated_utc TEXT NULL
                    );
                    CREATE TABLE IF NOT EXISTS adapter_topology (
                        id INTEGER PRIMARY KEY CHECK (id = 1),
                        payload TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS managed_root_names (
                        directory_name TEXT PRIMARY KEY COLLATE NOCASE,
                        root_id TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS managed_root_renames (
                        root_id TEXT PRIMARY KEY,
                        payload TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS managed_root_rename_history (
                        operation_id TEXT PRIMARY KEY,
                        root_id TEXT NOT NULL,
                        payload TEXT NOT NULL,
                        proof TEXT NULL,
                        observation TEXT NULL
                    );
                    CREATE TABLE IF NOT EXISTS journal_coalescing_plans (
                        plan_id TEXT PRIMARY KEY,
                        payload TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS journal_coalescing_members (
                        operation_id TEXT PRIMARY KEY,
                        plan_id TEXT NOT NULL REFERENCES journal_coalescing_plans(plan_id)
                    );
                    INSERT OR IGNORE INTO managed_root_rename_history (operation_id, root_id, payload)
                        SELECT json_extract(payload, '$.OperationId'), root_id, payload FROM managed_root_renames;
                    CREATE TABLE IF NOT EXISTS notification_snoozes (
                        conflict_id TEXT PRIMARY KEY,
                        snoozed_utc TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS credential_cleanup (
                        reference_id TEXT PRIMARY KEY,
                        provider TEXT NOT NULL,
                        created_utc TEXT NOT NULL
                    );
                    """;
                await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (SqliteCommand column = connection.CreateCommand())
            {
                column.CommandText = "SELECT 1 FROM pragma_table_info('instance_runtime') WHERE name = 'last_error_code';";
                if (await column.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    await using SqliteCommand alter = connection.CreateCommand();
                    alter.CommandText = "ALTER TABLE instance_runtime ADD COLUMN last_error_code TEXT NULL;";
                    await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            foreach ((string Name, string Definition) in new[]
            {
                ("transfer_operation", "TEXT NULL"),
                ("transfer_bytes", "INTEGER NULL"),
                ("transfer_total", "INTEGER NULL"),
                ("transfer_updated_utc", "TEXT NULL"),
            })
            {
                await using SqliteCommand column = connection.CreateCommand();
                column.CommandText = $"SELECT 1 FROM pragma_table_info('instance_runtime') WHERE name = '{Name}';";
                if (await column.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    await using SqliteCommand alter = connection.CreateCommand();
                    alter.CommandText = $"ALTER TABLE instance_runtime ADD COLUMN {Name} {Definition};";
                    await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using (SqliteCommand version = connection.CreateCommand())
            {
                await using SqliteCommand column = connection.CreateCommand();
                column.CommandText = "SELECT 1 FROM pragma_table_info('worker_requests') WHERE name = 'stable_fingerprint';";
                if (await column.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    await using SqliteCommand alter = connection.CreateCommand();
                    alter.CommandText = "ALTER TABLE worker_requests ADD COLUMN stable_fingerprint BLOB NULL;";
                    await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                version.CommandText = "PRAGMA user_version=19;";
                await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new(owner, connection);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            owner.Dispose();
            throw;
        }
    }

    /// <summary>Returns false for an identical replay and rejects reuse with a different payload.</summary>
    public Task<bool> TryRecordWorkerRequestAsync(Guid operationId, InstanceId instanceId,
        ReadOnlyMemory<byte> fingerprint, CancellationToken cancellationToken = default) =>
        TryRecordWorkerRequestAsync(operationId, instanceId, fingerprint, ReadOnlyMemory<byte>.Empty, cancellationToken);

    /// <summary>Also permits a verified stable payload whose official item reference changed.</summary>
    public async Task<bool> TryRecordWorkerRequestAsync(
        Guid operationId,
        InstanceId instanceId,
        ReadOnlyMemory<byte> fingerprint,
        ReadOnlyMemory<byte> stableFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The Worker operation ID cannot be empty.", nameof(operationId));
        }

        if (fingerprint.Length != 32 || !stableFingerprint.IsEmpty && stableFingerprint.Length != 32)
        {
            throw new ArgumentException("A Worker request fingerprint must be a SHA-256 digest.", nameof(fingerprint));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO worker_requests (operation_id, instance_id, fingerprint, stable_fingerprint)
                VALUES ($operation, $instance, $fingerprint, $stable)
                ON CONFLICT(operation_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$instance", instanceId.ToString());
            insert.Parameters.AddWithValue("$fingerprint", fingerprint.ToArray());
            insert.Parameters.AddWithValue("$stable", stableFingerprint.IsEmpty ? DBNull.Value : stableFingerprint.ToArray());
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                return true;
            }

            MirrorPulseWorkerRequestRecord existing = await ReadWorkerRequestCoreAsync(operationId, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The existing Worker request disappeared.");
            bool exactReplay = existing.Fingerprint.AsSpan().SequenceEqual(fingerprint.Span);
            bool stableReplay = !stableFingerprint.IsEmpty && existing.StableFingerprint is not null &&
                existing.StableFingerprint.AsSpan().SequenceEqual(stableFingerprint.Span);
            if (existing.InstanceId != instanceId ||
                (!exactReplay && !stableReplay) ||
                (!stableFingerprint.IsEmpty && existing.StableFingerprint is not null && !stableReplay))
            {
                throw new InvalidDataException("A Worker operation ID was reused with a different request.");
            }

            // A legacy fingerprint can be supplemented only after an exact replay.
            // Never manufacture a stable fingerprint for an already changed payload.
            if (exactReplay && !stableFingerprint.IsEmpty && existing.StableFingerprint is null)
            {
                await using SqliteCommand update = _connection.CreateCommand();
                update.CommandText = "UPDATE worker_requests SET stable_fingerprint=$stable WHERE operation_id=$operation;";
                update.Parameters.AddWithValue("$stable", stableFingerprint.ToArray());
                update.Parameters.AddWithValue("$operation", operationId.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MirrorPulseWorkerRequestRecord?> ReadWorkerRequestAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ReadWorkerRequestCoreAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveUserCommandAsync(
        MirrorPulseUserCommandRecord command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CommandId == Guid.Empty)
        {
            throw new ArgumentException("The user command ID cannot be empty.", nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TargetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.State);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO user_commands (command_id, action, target_id, state)
                VALUES ($id, $action, $target, $state)
                ON CONFLICT(command_id) DO UPDATE SET state = excluded.state
                WHERE user_commands.action = excluded.action
                  AND user_commands.target_id = excluded.target_id;
                """;
            insert.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            insert.Parameters.AddWithValue("$action", command.Action);
            insert.Parameters.AddWithValue("$target", command.TargetId);
            insert.Parameters.AddWithValue("$state", command.State);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidDataException("A user command ID was reused with different intent.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MirrorPulseUserCommandRecord?> ReadUserCommandAsync(
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = "SELECT action, target_id, state FROM user_commands WHERE command_id = $id;";
            query.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new(commandId, reader.GetString(0), reader.GetString(1), reader.GetString(2))
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stores UI metadata for a conflict whose authority remains in CfSharp.</summary>
    public async Task SaveRemoteConflictProjectionAsync(
        MirrorPulseConflictRecord conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (conflict.Source != MirrorPulseConflictSource.CfSharpRemote || !conflict.IsPending)
        {
            throw new ArgumentException("Only pending CfSharp remote conflicts can be projected.", nameof(conflict));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO remote_conflict_projections
                    (conflict_id, instance_id, change_id, relative_path, reason,
                     version_comparison, local_revision, remote_revision, detected_utc)
                VALUES ($id, $instance, $change, $path, $reason, $comparison, $local, $remote, $detected)
                ON CONFLICT(conflict_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", conflict.ConflictId.ToString("D"));
            command.Parameters.AddWithValue("$instance", conflict.InstanceId.ToString());
            command.Parameters.AddWithValue("$change", conflict.ChangeId);
            command.Parameters.AddWithValue("$path", conflict.RelativePath);
            command.Parameters.AddWithValue("$reason", (int)conflict.Reason);
            command.Parameters.AddWithValue("$comparison", (int)conflict.VersionComparison);
            command.Parameters.AddWithValue("$local", (object?)conflict.LocalRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$remote", (object?)conflict.RemoteRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$detected", conflict.DetectedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MirrorPulseConflictRecord>> ReadRemoteConflictProjectionsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = """
                SELECT conflict_id, instance_id, change_id, relative_path, reason,
                       version_comparison, local_revision, remote_revision, detected_utc
                FROM remote_conflict_projections ORDER BY detected_utc, conflict_id;
                """;
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var records = new List<MirrorPulseConflictRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(new MirrorPulseConflictRecord(
                    Guid.Parse(reader.GetString(0)),
                    InstanceId.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    (MirrorPulseConflictReason)reader.GetInt32(4),
                    (MirrorPulseVersionComparison)reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                    source: MirrorPulseConflictSource.CfSharpRemote));
            }

            return records;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveInstanceRuntimeStateAsync(
        MirrorPulseInstanceRuntimeState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Phase);
        if (state.InstanceId.Value == Guid.Empty)
        {
            throw new ArgumentException("The runtime state requires an Adapter instance ID.", nameof(state));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
            INSERT INTO instance_runtime
                    (instance_id, phase, requires_full_rescan, last_successful_sync_utc, last_error_code,
                     transfer_operation, transfer_bytes, transfer_total, transfer_updated_utc)
                VALUES ($instance, $phase, $rescan, $last, $error,
                        $operation, $bytes, $total, $updated)
                ON CONFLICT(instance_id) DO UPDATE SET
                    phase = excluded.phase,
                    requires_full_rescan = excluded.requires_full_rescan,
                    last_successful_sync_utc = excluded.last_successful_sync_utc,
                    last_error_code = COALESCE(excluded.last_error_code, instance_runtime.last_error_code),
                    transfer_operation = COALESCE(excluded.transfer_operation, instance_runtime.transfer_operation),
                    transfer_bytes = COALESCE(excluded.transfer_bytes, instance_runtime.transfer_bytes),
                    transfer_total = COALESCE(excluded.transfer_total, instance_runtime.transfer_total),
                    transfer_updated_utc = COALESCE(excluded.transfer_updated_utc, instance_runtime.transfer_updated_utc);
            """;
            command.Parameters.AddWithValue("$instance", state.InstanceId.ToString());
            command.Parameters.AddWithValue("$phase", state.Phase);
            command.Parameters.AddWithValue("$rescan", state.RequiresFullRescan ? 1 : 0);
            command.Parameters.AddWithValue("$last", state.LastSuccessfulSync is null
                ? DBNull.Value
                : state.LastSuccessfulSync.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$error", state.LastErrorCode is null
                ? DBNull.Value : state.LastErrorCode);
            command.Parameters.AddWithValue("$operation", state.TransferProgress is null
                ? DBNull.Value : state.TransferProgress.Operation);
            command.Parameters.AddWithValue("$bytes", state.TransferProgress is null
                ? DBNull.Value : state.TransferProgress.BytesTransferred);
            command.Parameters.AddWithValue("$total", state.TransferProgress?.TotalBytes is null
                ? DBNull.Value : state.TransferProgress.TotalBytes.Value);
            command.Parameters.AddWithValue("$updated", state.TransferProgress is null
                ? DBNull.Value
                : state.TransferProgress.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MirrorPulseInstanceRuntimeState?> ReadInstanceRuntimeStateAsync(
        InstanceId instanceId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = """
                SELECT phase, requires_full_rescan, last_successful_sync_utc, last_error_code,
                       transfer_operation, transfer_bytes, transfer_total, transfer_updated_utc
                FROM instance_runtime WHERE instance_id = $instance;
            """;
            query.Parameters.AddWithValue("$instance", instanceId.ToString());
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            MirrorPulseTransferProgress? progress = reader.IsDBNull(4) || reader.IsDBNull(5)
                ? null
                : new MirrorPulseTransferProgress(
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture));
            return new MirrorPulseInstanceRuntimeState(
                instanceId,
                reader.GetString(0),
                reader.GetInt32(1) != 0,
                reader.IsDBNull(2) ? null : DateTimeOffset.Parse(
                    reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                progress);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
            _owner.Dispose();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MirrorPulseWorkerRequestRecord?> ReadWorkerRequestCoreAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.CommandText = """
            SELECT instance_id, fingerprint, attempt, next_attempt_utc, stable_fingerprint
            FROM worker_requests WHERE operation_id = $operation;
            """;
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(
            operationId,
            InstanceId.Parse(reader.GetString(0)),
            (byte[])reader.GetValue(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
