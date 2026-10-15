using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseConflictRecord?> ReadUploadConflictAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default)
    {
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("The conflict ID cannot be empty.", nameof(conflictId));
        }

        IReadOnlyList<MirrorPulseConflictRecord> records =
            await ReadUploadConflictsAsync(pendingOnly: false, cancellationToken).ConfigureAwait(false);
        return records.SingleOrDefault(item => item.ConflictId == conflictId);
    }

    public async Task SetUploadConflictStatusAsync(
        Guid conflictId,
        MirrorPulseConflictStatus status,
        CancellationToken cancellationToken = default)
    {
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("The conflict ID cannot be empty.", nameof(conflictId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE upload_conflicts SET status = $status
                WHERE conflict_id = $id;
                """;
            command.Parameters.AddWithValue("$id", conflictId.ToString("D"));
            command.Parameters.AddWithValue("$status", (int)status);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new FileNotFoundException("The upload conflict was not found.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Persists an upload conflict independently of the CfSharp journal operation.</summary>
    public async Task SaveUploadConflictAsync(
        MirrorPulseConflictRecord conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (conflict.Source != MirrorPulseConflictSource.Upload || !conflict.IsPending)
        {
            throw new ArgumentException("Only pending upload conflicts can be recorded.", nameof(conflict));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO upload_conflicts
                    (conflict_id, instance_id, change_id, relative_path, reason,
                     version_comparison, local_revision, remote_revision, detected_utc, status)
                VALUES ($id, $instance, $change, $path, $reason,
                        $comparison, $local, $remote, $detected, $status)
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
            command.Parameters.AddWithValue("$detected", conflict.DetectedAt.ToString("O",
                System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$status", (int)conflict.Status);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasPendingUploadConflictAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The journal operation ID cannot be empty.", nameof(operationId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = """
                SELECT 1 FROM upload_conflicts
                WHERE (conflict_id = $id OR change_id = $id) AND status = $pending LIMIT 1;
                """;
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            query.Parameters.AddWithValue("$pending", (int)MirrorPulseConflictStatus.Pending);
            return await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MirrorPulseConflictRecord>> ReadUploadConflictsAsync(
        bool pendingOnly = true,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand query = _connection.CreateCommand();
            query.CommandText = pendingOnly
                ? """
                    SELECT conflict_id, instance_id, change_id, relative_path, reason,
                           version_comparison, local_revision, remote_revision, detected_utc, status
                    FROM upload_conflicts WHERE status = $pending ORDER BY detected_utc, conflict_id;
                    """
                : """
                    SELECT conflict_id, instance_id, change_id, relative_path, reason,
                           version_comparison, local_revision, remote_revision, detected_utc, status
                    FROM upload_conflicts ORDER BY detected_utc, conflict_id;
                    """;
            if (pendingOnly)
            {
                query.Parameters.AddWithValue("$pending", (int)MirrorPulseConflictStatus.Pending);
            }

            await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
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
                    DateTimeOffset.Parse(reader.GetString(8),
                        System.Globalization.CultureInfo.InvariantCulture),
                    (MirrorPulseConflictStatus)reader.GetInt32(9),
                    MirrorPulseConflictSource.Upload));
            }

            return records;
        }
        finally
        {
            _gate.Release();
        }
    }
}
