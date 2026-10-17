using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Finds an original remote move by its durable entry owner, including completed and rejected history.</summary>
    /// <remarks>The indexed owner and the full retained move are validated in one read transaction.
    /// A lookup never adopts the object currently occupying a source or target path.</remarks>
    public async Task<MirrorPulseNamespaceMemberMoveIntent?> ReadNamespaceRemoteMoveAsync(
        InstanceId instanceId, string batchId, int ordinal, CancellationToken cancellationToken = default)
    {
        if (instanceId.Value == Guid.Empty || string.IsNullOrWhiteSpace(batchId) || batchId.Length > 16_384 ||
            batchId.Contains('\0') || !batchId.StartsWith($"{instanceId}/", StringComparison.Ordinal) ||
            batchId.Length == instanceId.ToString().Length + 1 || ordinal < 0)
            throw new ArgumentException("A remote move lookup requires its bounded original entry owner.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            Guid? operation = null;
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT operation_id FROM namespace_remote_move_entries
                    WHERE instance_id=$instance AND batch_id=$batch AND ordinal=$ordinal;
                    """;
                query.Parameters.AddWithValue("$instance", instanceId.ToString());
                query.Parameters.AddWithValue("$batch", batchId);
                query.Parameters.AddWithValue("$ordinal", ordinal);
                if (await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string stored)
                    operation = ParseNamespaceMemberId(stored);
            }
            if (operation is null) { transaction.Commit(); return null; }
            var intent = await ReadNamespaceMemberMoveCoreAsync(operation.Value, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The remote entry index lost its original move admission.");
            if (intent.RemoteEntry is not { } remote || remote.InstanceId != instanceId || remote.BatchId != batchId || remote.Ordinal != ordinal)
                throw new InvalidDataException("The remote entry lookup disagrees with its retained original owner.");
            transaction.Commit(); return intent;
        }
        finally { _gate.Release(); }
    }

    private async Task InsertNamespaceRemoteMoveReferenceAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (intent.RemoteEntry is not { } remote) return;
        await using var previous = _connection.CreateCommand(); previous.Transaction = transaction;
        previous.CommandText = """
            SELECT batch_fingerprint,entry_count,ordinal,change_id FROM namespace_remote_move_entries
            WHERE instance_id=$instance AND batch_id=$batch
              AND (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            UNION ALL SELECT batch_fingerprint,entry_count,ordinal,change_id FROM namespace_remote_birth_entries
            WHERE instance_id=$instance AND batch_id=$batch
              AND (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            UNION ALL SELECT batch_fingerprint,entry_count,ordinal,change_id FROM namespace_remote_retirement_entries
            WHERE instance_id=$instance AND batch_id=$batch
              AND (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            LIMIT 1;
            """;
        AddNamespaceRemoteMoveParameters(previous, intent);
        if (await previous.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A remote batch entry already owns a move or disagrees with its retained batch.");
        await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO namespace_remote_move_entries(operation_id,instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id)
            VALUES($operation,$instance,$batch,$hash,$count,$ordinal,$change);
            """;
        AddNamespaceRemoteMoveParameters(insert, intent);
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task ValidateNamespaceRemoteMoveReferenceIndexAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id
            FROM namespace_remote_move_entries WHERE operation_id=$operation;
            """;
        query.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        bool exists = await reader.ReadAsync(token).ConfigureAwait(false);
        if (intent.RemoteEntry is not { } remote)
        {
            if (exists) throw new InvalidDataException("An ordinary controlled move cannot acquire a remote owner.");
            return;
        }
        if (!exists || reader.GetString(0) != remote.InstanceId.ToString() || reader.GetString(1) != remote.BatchId ||
            reader.GetString(2) != remote.BatchFingerprint || reader.GetInt32(3) != remote.BatchEntryCount ||
            reader.GetInt32(4) != remote.Ordinal || reader.GetString(5) != remote.ChangeId)
            throw new InvalidDataException("The remote move owner index disagrees with its immutable batch admission.");
        await reader.DisposeAsync().ConfigureAwait(false);
        await ValidateNamespaceRemoteEntryCrossKindAsync(intent.OperationId, remote.InstanceId, remote.BatchId,
            remote.BatchFingerprint, remote.BatchEntryCount, remote.Ordinal, remote.ChangeId, 0, transaction, token).ConfigureAwait(false);
    }

    private static void AddNamespaceRemoteMoveParameters(SqliteCommand command, MirrorPulseNamespaceMemberMoveIntent intent)
    {
        var remote = intent.RemoteEntry!;
        command.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$instance", remote.InstanceId.ToString());
        command.Parameters.AddWithValue("$batch", remote.BatchId);
        command.Parameters.AddWithValue("$hash", remote.BatchFingerprint);
        command.Parameters.AddWithValue("$count", remote.BatchEntryCount);
        command.Parameters.AddWithValue("$ordinal", remote.Ordinal);
        command.Parameters.AddWithValue("$change", remote.ChangeId);
    }

    private static async Task EnsureNamespaceRemoteMoveSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_move_entries (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
                instance_id TEXT NOT NULL,
                batch_id TEXT NOT NULL,
                batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
                entry_count INTEGER NOT NULL CHECK(entry_count>0),
                ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count),
                change_id TEXT NOT NULL,
                UNIQUE(instance_id,batch_id,ordinal),
                UNIQUE(instance_id,batch_id,change_id)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
