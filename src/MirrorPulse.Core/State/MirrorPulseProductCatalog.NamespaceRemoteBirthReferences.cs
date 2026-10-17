using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>The public apply policy retained before SDK-owned native creation, not a successful native outcome.</summary>
public sealed record MirrorPulseRemoteBirthPolicy(bool PreserveUnsynchronizedLocalContent, bool SuppressLocalEcho,
    long EchoSuppressionLifetimeTicks, int EchoSuppressionObservationCount);

/// <summary>Original batch ownership and logical identity; the child has no native binding at admission.</summary>
public sealed record MirrorPulseNamespaceRemoteBirthReference(int Version, InstanceId InstanceId, string BatchId,
    string BatchFingerprint, int BatchEntryCount, int Ordinal, string ChangeId, Guid ItemId, string RemoteId,
    string RemoteRevision, Guid ParentMemberId, Guid? ParentPositionOperationId, string PackageVersion,
    string PackageSource, MirrorPulseRemoteBirthPolicy Policy);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads a bounded set of original unregistered birth owners for one exact batch, without scanning history.</summary>
    public async Task<IReadOnlyList<MirrorPulseNamespaceBirthIntent>> ReadPendingNamespaceRemoteBirthsAsync(InstanceId instanceId,
        string batchId, CancellationToken cancellationToken = default)
    {
        ValidateRemoteBirthLookup(instanceId, batchId, 0);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var ids = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT r.operation_id FROM namespace_remote_birth_entries r
                    LEFT JOIN namespace_members m ON m.birth_operation_id=r.operation_id
                    WHERE r.instance_id=$instance AND r.batch_id=$batch AND m.member_id IS NULL
                    ORDER BY r.ordinal LIMIT 65;
                    """;
                query.Parameters.AddWithValue("$instance", instanceId.ToString()); query.Parameters.AddWithValue("$batch", batchId);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            if (ids.Count > 64) throw new InvalidDataException("The original remote batch has too many unresolved birth owners.");
            var births = new List<MirrorPulseNamespaceBirthIntent>(ids.Count);
            foreach (var id in ids)
            {
                var birth = await ReadNamespaceBirthCoreAsync(id, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The pending remote birth lost its original admission.");
                if (birth.RemoteEntry is not { } remote || remote.InstanceId != instanceId || remote.BatchId != batchId)
                    throw new InvalidDataException("The pending remote birth index changed its owner.");
                await RequireNamespaceBirthReservationAsync(birth, transaction, cancellationToken).ConfigureAwait(false);
                births.Add(birth);
            }
            transaction.Commit(); return births.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads original remote birth admission by immutable entry ownership, including registered history.</summary>
    public async Task<MirrorPulseNamespaceBirthIntent?> ReadNamespaceRemoteBirthAsync(InstanceId instanceId,
        string batchId, int ordinal, CancellationToken cancellationToken = default)
    {
        ValidateRemoteBirthLookup(instanceId, batchId, ordinal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            Guid? operation = null;
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT operation_id FROM namespace_remote_birth_entries WHERE instance_id=$instance AND batch_id=$batch AND ordinal=$ordinal;";
                query.Parameters.AddWithValue("$instance", instanceId.ToString()); query.Parameters.AddWithValue("$batch", batchId);
                query.Parameters.AddWithValue("$ordinal", ordinal);
                if (await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string stored)
                    operation = ParseNamespaceMemberId(stored);
            }
            if (operation is null) { transaction.Commit(); return null; }
            var birth = await ReadNamespaceBirthCoreAsync(operation.Value, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The remote birth entry lost its original admission.");
            if (birth.RemoteEntry is not { } remote || remote.InstanceId != instanceId || remote.BatchId != batchId || remote.Ordinal != ordinal)
                throw new InvalidDataException("The remote birth lookup disagrees with its original entry owner.");
            transaction.Commit(); return birth;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateRemoteBirthLookup(InstanceId instance, string batch, int ordinal)
    {
        if (instance.Value == Guid.Empty || string.IsNullOrWhiteSpace(batch) || batch.Length > 16_384 || batch.Contains('\0') ||
            !batch.StartsWith(instance + "/", StringComparison.Ordinal) || batch.Length == instance.ToString().Length + 1 || ordinal < 0)
            throw new ArgumentException("A remote birth requires a bounded original instance and batch entry owner.");
    }

    private static void ValidateNamespaceRemoteBirthReference(MirrorPulseNamespaceBirthIntent birth)
    {
        var remote = birth.RemoteEntry!;
        ValidateRemoteBirthLookup(remote.InstanceId, remote.BatchId, remote.Ordinal);
        if (birth.Origin != MirrorPulseNamespaceBirthOrigin.RemotePopulation || remote.Version != 1 || remote.ItemId == Guid.Empty ||
            remote.ParentMemberId == Guid.Empty || remote.ParentMemberId == birth.OperationId || remote.ParentPositionOperationId == Guid.Empty ||
            remote.BatchFingerprint is not { Length: 64 } || remote.BatchFingerprint.Any(character => !char.IsAsciiHexDigitUpper(character)) ||
            remote.BatchEntryCount is < 1 or > 65_536 || remote.Ordinal >= remote.BatchEntryCount ||
            string.IsNullOrWhiteSpace(remote.ChangeId) || remote.ChangeId.Length > 16_384 || remote.ChangeId.Contains('\0') ||
            string.IsNullOrWhiteSpace(remote.RemoteId) || remote.RemoteId.Length > 4096 || remote.RemoteId.Contains('\0') ||
            string.IsNullOrWhiteSpace(remote.RemoteRevision) || remote.RemoteRevision.Length > 4096 || remote.RemoteRevision.Contains('\0') ||
            remote.PackageVersion != "0.1.0-preview.6" || remote.PackageSource != "3caa382e341d260543d3afc05e25fa034fb32e16" ||
            remote.Policy is not { } policy || policy.EchoSuppressionLifetimeTicks <= 0 ||
            policy.EchoSuppressionLifetimeTicks > TimeSpan.FromHours(1).Ticks || policy.EchoSuppressionObservationCount is < 1 or > 64)
            throw new ArgumentException("Remote birth admission requires its exact original identity, supported SDK protocol and bounded public policy.", nameof(birth));
    }

    private async Task ValidateNamespaceRemoteBirthParentAsync(MirrorPulseNamespaceBirthIntent birth,
        SqliteTransaction transaction, CancellationToken token)
    {
        var remote = birth.RemoteEntry!;
        var parent = await ReadNamespaceMemberCoreAsync(remote.ParentMemberId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The remote birth parent is not registered.");
        var location = await ReadNamespaceMemberLocationCoreAsync(parent, transaction, token).ConfigureAwait(false);
        int separator = birth.RelativePath.LastIndexOf('/');
        if (!parent.IsDirectory || parent.RootId != birth.RootId || parent.LocalObject != birth.ParentLocalObject || separator <= 0 ||
            location.RelativePath != birth.RelativePath[..separator] || location.CurrentPositionOperationId != remote.ParentPositionOperationId ||
            (parent.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal ?
                birth.BornParent is not null || birth.ParentEvidenceId != parent.OriginId :
                birth.BornParent?.BirthOperationId != parent.OriginId) ||
            await ReadCompletedNamespaceRetirementForMemberCoreAsync(parent.MemberId, transaction, token).ConfigureAwait(false))
            throw new InvalidOperationException("A remote birth requires its exact current direct parent member and placement.");
        await RequireNamespaceMemberNameAsync(parent, transaction, token).ConfigureAwait(false);
    }

    private async Task InsertNamespaceRemoteBirthReferenceAsync(MirrorPulseNamespaceBirthIntent birth,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (birth.RemoteEntry is null) return;
        await using (var identity = _connection.CreateCommand())
        {
            identity.Transaction = transaction;
            identity.CommandText = """
                SELECT 1 FROM namespace_permission_local_identities WHERE item_id=$item
                UNION ALL SELECT 1 FROM namespace_birth_plans WHERE item_id=$item
                UNION ALL SELECT 1 FROM namespace_remote_birth_entries WHERE item_id=$item LIMIT 1;
                """;
            identity.Parameters.AddWithValue("$item", birth.RemoteEntry.ItemId.ToString("D"));
            if (await identity.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("The remote birth identity is already owned by another original object or birth.");
        }
        await using var previous = _connection.CreateCommand(); previous.Transaction = transaction;
        previous.CommandText = """
            SELECT operation_id FROM namespace_remote_birth_entries
            WHERE instance_id=$instance AND batch_id=$batch
              AND (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            UNION ALL SELECT operation_id FROM namespace_remote_move_entries
            WHERE instance_id=$instance AND batch_id=$batch
              AND (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            UNION ALL SELECT operation_id FROM namespace_remote_retirement_entries
            WHERE instance_id=$instance AND batch_id=$batch
              AND (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            LIMIT 1;
            """;
        AddNamespaceRemoteBirthParameters(previous, birth);
        if (await previous.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("Another namespace admission owns this remote entry or disagrees with its original batch.");
        await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO namespace_remote_birth_entries(operation_id,instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id,item_id)
            VALUES($operation,$instance,$batch,$hash,$count,$ordinal,$change,$item);
            """;
        AddNamespaceRemoteBirthParameters(insert, birth);
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task ValidateNamespaceRemoteBirthReferenceIndexAsync(MirrorPulseNamespaceBirthIntent birth,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id,item_id FROM namespace_remote_birth_entries WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", birth.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        bool exists = await reader.ReadAsync(token).ConfigureAwait(false);
        if (birth.RemoteEntry is not { } remote)
        {
            if (exists) throw new InvalidDataException("An ordinary birth cannot acquire original batch ownership.");
            return;
        }
        if (!exists || reader.GetString(0) != remote.InstanceId.ToString() || reader.GetString(1) != remote.BatchId ||
            reader.GetString(2) != remote.BatchFingerprint || reader.GetInt32(3) != remote.BatchEntryCount ||
            reader.GetInt32(4) != remote.Ordinal || reader.GetString(5) != remote.ChangeId || reader.GetString(6) != remote.ItemId.ToString("D"))
            throw new InvalidDataException("The remote birth owner index disagrees with its immutable admission.");
        await reader.DisposeAsync().ConfigureAwait(false);
        await ValidateNamespaceRemoteEntryCrossKindAsync(birth.OperationId, remote.InstanceId, remote.BatchId,
            remote.BatchFingerprint, remote.BatchEntryCount, remote.Ordinal, remote.ChangeId, 1, transaction, token).ConfigureAwait(false);
    }

    private async Task ValidateNamespaceRemoteEntryCrossKindAsync(Guid operationId, InstanceId instanceId, string batchId,
        string fingerprint, int count, int ordinal, string changeId, int ownerKind, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT operation_id FROM namespace_remote_birth_entries WHERE instance_id=$instance AND batch_id=$batch
              AND (operation_id<>$operation OR $kind<>1) AND
              (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            UNION ALL SELECT operation_id FROM namespace_remote_move_entries WHERE instance_id=$instance AND batch_id=$batch
              AND (operation_id<>$operation OR $kind<>0) AND
              (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            UNION ALL SELECT operation_id FROM namespace_remote_retirement_entries WHERE instance_id=$instance AND batch_id=$batch
              AND (operation_id<>$operation OR $kind<>2) AND
              (batch_fingerprint<>$hash OR entry_count<>$count OR ordinal=$ordinal OR change_id=$change)
            LIMIT 1;
            """;
        query.Parameters.AddWithValue("$operation", operationId.ToString("D")); query.Parameters.AddWithValue("$instance", instanceId.ToString());
        query.Parameters.AddWithValue("$batch", batchId); query.Parameters.AddWithValue("$hash", fingerprint);
        query.Parameters.AddWithValue("$count", count); query.Parameters.AddWithValue("$ordinal", ordinal); query.Parameters.AddWithValue("$change", changeId);
        query.Parameters.AddWithValue("$kind", ownerKind);
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("The retained remote namespace entry has conflicting batch ownership.");
    }

    private static void AddNamespaceRemoteBirthParameters(SqliteCommand command, MirrorPulseNamespaceBirthIntent birth)
    {
        var remote = birth.RemoteEntry!;
        command.Parameters.AddWithValue("$operation", birth.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$instance", remote.InstanceId.ToString()); command.Parameters.AddWithValue("$batch", remote.BatchId);
        command.Parameters.AddWithValue("$hash", remote.BatchFingerprint); command.Parameters.AddWithValue("$count", remote.BatchEntryCount);
        command.Parameters.AddWithValue("$ordinal", remote.Ordinal); command.Parameters.AddWithValue("$change", remote.ChangeId);
        command.Parameters.AddWithValue("$item", remote.ItemId.ToString("D"));
    }

    private static async Task EnsureNamespaceRemoteBirthSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_birth_entries (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_birth_intents(operation_id),
                instance_id TEXT NOT NULL, batch_id TEXT NOT NULL,
                batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
                entry_count INTEGER NOT NULL CHECK(entry_count>0),
                ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count), change_id TEXT NOT NULL,
                item_id TEXT NOT NULL UNIQUE,
                UNIQUE(instance_id,batch_id,ordinal), UNIQUE(instance_id,batch_id,change_id)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
