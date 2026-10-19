using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>The immutable original SDK delete and its registered child and direct parent.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementReference(int Version, InstanceId InstanceId,
    string BatchId, string BatchFingerprint, int BatchEntryCount, int Ordinal, string ChangeId,
    Guid ItemId, string RemoteId, string? PreviousRemoteRevision, string? RemoteRevision,
    string? NativeRemoteRevision, string? OfficialRemoteRevision,
    MirrorPulseNamespaceMemberProtectionSnapshot Child, MirrorPulseNamespaceMemberProtectionSnapshot Parent,
    string PackageVersion, string PackageSource, MirrorPulseRemoteBirthPolicy Policy);

/// <summary>Fresh facts after an actual original SDK outcome, without resolving its durable conflict.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementOutcome(int Version, Guid OperationId,
    Guid? ConflictId, DateTimeOffset? ConflictDetectedAt, DateTimeOffset VerifiedAt,
    bool NativeMissing, bool OfficialTombstone, string? OfficialRemoteRevision,
    MirrorPulseNamespacePermissionVerification Parent, MirrorPulseNamespacePermissionVerification? Child);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<bool> CanResumeNamespaceRemoteRetirementAsync(MirrorPulseNamespaceRetirementIntent original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.RemoteEntry is null) throw new ArgumentException("Recovery requires an original SDK retirement.", nameof(original));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadNamespaceRetirementCoreAsync(original.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (retained?.Intent != original || retained.Phase != MirrorPulseNamespaceRetirementPhase.Prepared) { transaction.Commit(); return false; }
            var fenced = await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken,
                admittedRemoteRetirementId: original.OperationId).ConfigureAwait(false);
            transaction.Commit(); return !fenced.Contains(original.RootId);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MirrorPulseNamespaceRetirement>> ReadPendingNamespaceRemoteRetirementsAsync(InstanceId instanceId,
        string batchId, CancellationToken cancellationToken = default)
    {
        ValidateRemoteBirthLookup(instanceId, batchId, 0);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var operations = new List<Guid>();
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT e.operation_id FROM namespace_remote_retirement_entries e
                    JOIN namespace_member_retirements r ON r.operation_id=e.operation_id
                    WHERE e.instance_id=$instance AND e.batch_id=$batch AND r.phase NOT IN (2,4) ORDER BY e.ordinal LIMIT 65;
                    """;
                query.Parameters.AddWithValue("$instance", instanceId.ToString()); query.Parameters.AddWithValue("$batch", batchId);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) operations.Add(ParseNamespaceMemberId(reader.GetString(0)));
            }
            if (operations.Count > 64) throw new InvalidDataException("The original batch has too many unresolved retirement owners.");
            var result = new List<MirrorPulseNamespaceRetirement>(operations.Count);
            foreach (var operation in operations)
            {
                var retained = await ReadNamespaceRetirementCoreAsync(operation, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The pending remote retirement lost its original admission.");
                if (retained.Intent.RemoteEntry is not { } remote || remote.InstanceId != instanceId || remote.BatchId != batchId)
                    throw new InvalidDataException("The pending remote retirement changed its original owner.");
                result.Add(retained);
            }
            transaction.Commit(); return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRetirement?> ReadNamespaceRemoteRetirementAsync(InstanceId instanceId,
        string batchId, int ordinal, CancellationToken cancellationToken = default)
    {
        ValidateRemoteBirthLookup(instanceId, batchId, ordinal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT operation_id FROM namespace_remote_retirement_entries WHERE instance_id=$instance AND batch_id=$batch AND ordinal=$ordinal;";
            query.Parameters.AddWithValue("$instance", instanceId.ToString()); query.Parameters.AddWithValue("$batch", batchId);
            query.Parameters.AddWithValue("$ordinal", ordinal);
            var operation = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            var result = operation is null ? null : await ReadNamespaceRetirementCoreAsync(ParseNamespaceMemberId(operation), transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The remote retirement index lost its original admission.");
            if (result is not null && (result.Intent.RemoteEntry is not { } remote || remote.InstanceId != instanceId || remote.BatchId != batchId || remote.Ordinal != ordinal))
                throw new InvalidDataException("The remote retirement lookup changed its original entry owner.");
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteRetirementOutcome?> ReadNamespaceRemoteRetirementOutcomeAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original retirement ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var original = await ReadNamespaceRetirementCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var outcome = await ReadRemoteRetirementOutcomeCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (outcome is not null)
                ValidateRemoteRetirementOutcome(original ?? throw new InvalidDataException("The remote outcome lost its original retirement."), outcome);
            transaction.Commit(); return outcome;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically retains the actual SDK outcome and releases only its original member admission.</summary>
    /// <remarks>The caller must verify the original public SDK prefix, decoded conflict or tombstone,
    /// actual native absence or unchanged child, and its retained protected parent under the Host actor.</remarks>
    public async Task<MirrorPulseNamespaceRetirement> SettleNamespaceRemoteRetirementAsync(
        MirrorPulseNamespaceRemoteRetirementOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var original = await ReadNamespaceRetirementCoreAsync(outcome.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote retirement is missing.");
            ValidateRemoteRetirementOutcome(original, outcome);
            var replay = await ReadRemoteRetirementOutcomeCoreAsync(outcome.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != outcome) throw new InvalidOperationException("A remote retirement outcome is immutable.");
                if (original.Phase != (outcome.ConflictId is null ? MirrorPulseNamespaceRetirementPhase.Completed : MirrorPulseNamespaceRetirementPhase.RemoteRejected))
                    throw new InvalidDataException("The retained outcome disagrees with its terminal retirement.");
                transaction.Commit(); return original;
            }
            if (original.Phase != MirrorPulseNamespaceRetirementPhase.Prepared)
                throw new InvalidOperationException("Only the original pending SDK retirement can settle.");
            await ValidateRemoteRetirementCurrentAsync(original.Intent, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(original.Intent.RootId, null, transaction, cancellationToken,
                allowedRetirement: original.OperationId).ConfigureAwait(false);
            if ((await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken,
                admittedRemoteRetirementId: original.OperationId).ConfigureAwait(false)).Contains(original.Intent.RootId))
                throw new InvalidOperationException("Another namespace owner prevents original SDK retirement settlement.");
            await RequireNoRegisteredDescendantsAsync(original.Intent.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.SerializeToUtf8Bytes(outcome, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("Remote retirement evidence must be bounded.", nameof(outcome));
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_remote_retirement_outcomes(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
            insert.Parameters.AddWithValue("$id", outcome.OperationId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload)); await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.ConflictId is null)
            {
                await using var remove = _connection.CreateCommand(); remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;";
                remove.Parameters.AddWithValue("$member", original.Intent.MemberId.ToString("D"));
                if (await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("The remote retirement lost its original member name.");
            }
            var terminal = original with
            {
                Phase = outcome.ConflictId is null ? MirrorPulseNamespaceRetirementPhase.Completed : MirrorPulseNamespaceRetirementPhase.RemoteRejected,
                NativeDeletedAt = outcome.ConflictId is null ? outcome.VerifiedAt : null,
                CompletedAt = outcome.VerifiedAt
            };
            await WriteNamespaceRetirementCoreAsync(terminal, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return terminal;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateRemoteRetirementOutcome(MirrorPulseNamespaceRetirement original,
        MirrorPulseNamespaceRemoteRetirementOutcome outcome)
    {
        var remote = original.Intent.RemoteEntry ?? throw new InvalidOperationException("The retirement has no original SDK owner.");
        if (outcome.Version != 1 || outcome.OperationId != original.OperationId || original.StartedAt is null ||
            original.StartedItemId != remote.ItemId || outcome.VerifiedAt < original.StartedAt || outcome.ConflictId == Guid.Empty ||
            outcome.Parent is null || outcome.Parent.LocalObject != remote.Parent.Context.Member.LocalObject ||
            outcome.Parent.OwnerSid != original.Intent.OwnerSid || outcome.Parent.Dacl != remote.Parent.Context.Verification.Dacl ||
            outcome.Parent.ObservedAt < original.StartedAt || outcome.Parent.ObservedAt > outcome.VerifiedAt)
            throw new InvalidOperationException("Remote retirement settlement requires its original start and fresh unchanged parent.");
        if (outcome.ConflictId is null)
        {
            if (!outcome.NativeMissing || !outcome.OfficialTombstone || outcome.Child is not null || outcome.ConflictDetectedAt is not null ||
                outcome.OfficialRemoteRevision != remote.RemoteRevision)
                throw new InvalidOperationException("An applied remote deletion requires native absence and its exact public SDK tombstone.");
        }
        else if (outcome.NativeMissing || outcome.OfficialTombstone || outcome.ConflictDetectedAt is not { } detected ||
            detected < original.StartedAt || detected > outcome.VerifiedAt || outcome.OfficialRemoteRevision != remote.OfficialRemoteRevision ||
            outcome.Child is not { } child || child.LocalObject != original.Intent.LocalObject || child.OwnerSid != original.Intent.OwnerSid ||
            child.Dacl != remote.Child.Context.Verification.Dacl || child.ObservedAt < detected || child.ObservedAt > outcome.VerifiedAt ||
            outcome.Parent.ObservedAt < detected)
            throw new InvalidOperationException("A rejected remote deletion must retain its actual conflict and unchanged original child.");
    }

    private async Task<MirrorPulseNamespaceRemoteRetirementOutcome?> ReadRemoteRetirementOutcomeCoreAsync(Guid operation,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint FROM namespace_remote_retirement_outcomes WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operation.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader[0], hash = (byte[])reader[1];
        if (payload.Length > 131_072 || hash.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw new InvalidDataException("The original remote retirement outcome is corrupt.");
        var result = JsonSerializer.Deserialize<MirrorPulseNamespaceRemoteRetirementOutcome>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The remote retirement outcome is empty.");
        if (result.OperationId != operation || !JsonSerializer.SerializeToUtf8Bytes(result, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("The remote retirement outcome changed its original owner.");
        return result;
    }

    private static void ValidateRemoteRetirementReference(MirrorPulseNamespaceRetirementIntent intent)
    {
        var remote = intent.RemoteEntry!;
        ValidateRemoteBirthLookup(remote.InstanceId, remote.BatchId, remote.Ordinal);
        if (remote.Version != 1 || remote.BatchEntryCount is < 1 or > 65_536 || remote.Ordinal >= remote.BatchEntryCount ||
            remote.BatchFingerprint is not { Length: 64 } || remote.BatchFingerprint.Any(character => !char.IsAsciiHexDigitUpper(character)) ||
            remote.ItemId == Guid.Empty || string.IsNullOrWhiteSpace(remote.ChangeId) || remote.ChangeId.Length > 16_384 || remote.ChangeId.Contains('\0') ||
            string.IsNullOrWhiteSpace(remote.RemoteId) || remote.RemoteId.Length > 4096 || remote.RemoteId.Contains('\0') ||
            new[] { remote.RemoteRevision, remote.PreviousRemoteRevision, remote.NativeRemoteRevision, remote.OfficialRemoteRevision }
                .Any(value => value is not null && (value.Length > 4096 || value.Contains('\0'))) ||
            remote.Child is not { } child || remote.Parent is not { } parent || child.Context.Member.MemberId != intent.MemberId ||
            child.Context.Member.LocalObject != intent.LocalObject || child.Context.Member.RootId != intent.RootId ||
            child.Context.Member.IsDirectory != intent.IsDirectory || child.Context.Member.OwnerSid != intent.OwnerSid ||
            child.Context.Location.RelativePath != intent.RelativePath || child.Context.Location.ParentMemberId != parent.Context.Member.MemberId ||
            parent.Context.Member.RootId != intent.RootId || !parent.Context.Member.IsDirectory || parent.Context.Member.OwnerSid != intent.OwnerSid ||
            child.Context.Protection.RoleSid != intent.RoleSid || parent.Context.Protection.RoleSid != intent.RoleSid ||
            child.Identity.ItemId != remote.ItemId || child.Identity.RemoteId != remote.RemoteId ||
            child.Context.Verification.ObservedAt > intent.PreparedAt || parent.Context.Verification.ObservedAt > intent.PreparedAt ||
            !MirrorPulseRemoteLibraryProvenance.IsSupported(remote.PackageVersion, remote.PackageSource) ||
            remote.Policy is not { PreserveUnsynchronizedLocalContent: true } policy || policy.EchoSuppressionLifetimeTicks <= 0 ||
            policy.EchoSuppressionLifetimeTicks > TimeSpan.FromHours(1).Ticks || policy.EchoSuppressionObservationCount is < 1 or > 64)
            throw new ArgumentException("Remote retirement requires its complete original SDK entry and protected member snapshots.", nameof(intent));
    }

    private async Task ValidateRemoteRetirementCurrentAsync(MirrorPulseNamespaceRetirementIntent intent, SqliteTransaction transaction, CancellationToken token)
    {
        if (intent.RemoteEntry is not { } remote) return;
        if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(intent.MemberId, transaction, token).ConfigureAwait(false) != remote.Child ||
            await ReadNamespaceMemberProtectionSnapshotCoreAsync(remote.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != remote.Parent)
            throw new InvalidOperationException("The original remote retirement lost its current child, parent, identity or protection.");
    }

    private async Task InsertRemoteRetirementReferenceAsync(MirrorPulseNamespaceRetirementIntent intent, SqliteTransaction transaction, CancellationToken token)
    {
        if (intent.RemoteEntry is null) return;
        await ValidateRemoteRetirementCurrentAsync(intent, transaction, token).ConfigureAwait(false);
        await ValidateRemoteRetirementIndexAsync(intent, transaction, token, inserting: true).ConfigureAwait(false);
        await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO namespace_remote_retirement_entries(operation_id,instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id) VALUES($operation,$instance,$batch,$hash,$count,$ordinal,$change);";
        AddRemoteRetirementParameters(insert, intent); await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task ValidateRemoteRetirementIndexAsync(MirrorPulseNamespaceRetirementIntent intent, SqliteTransaction? transaction,
        CancellationToken token, bool inserting = false)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT instance_id,batch_id,batch_fingerprint,entry_count,ordinal,change_id FROM namespace_remote_retirement_entries WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        bool exists = await reader.ReadAsync(token).ConfigureAwait(false);
        if (intent.RemoteEntry is not { } remote)
        {
            if (exists) throw new InvalidDataException("An ordinary retirement cannot acquire an SDK batch owner.");
            return;
        }
        if (inserting ? exists : !exists || reader.GetString(0) != remote.InstanceId.ToString() || reader.GetString(1) != remote.BatchId ||
            reader.GetString(2) != remote.BatchFingerprint || reader.GetInt32(3) != remote.BatchEntryCount || reader.GetInt32(4) != remote.Ordinal || reader.GetString(5) != remote.ChangeId)
            throw new InvalidDataException("The remote retirement index disagrees with its original admission.");
        await reader.DisposeAsync().ConfigureAwait(false);
        await ValidateNamespaceRemoteEntryCrossKindAsync(intent.OperationId, remote.InstanceId, remote.BatchId,
            remote.BatchFingerprint, remote.BatchEntryCount, remote.Ordinal, remote.ChangeId, 2, transaction, token).ConfigureAwait(false);
    }

    private static void AddRemoteRetirementParameters(SqliteCommand command, MirrorPulseNamespaceRetirementIntent intent)
    {
        var remote = intent.RemoteEntry!;
        command.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D")); command.Parameters.AddWithValue("$instance", remote.InstanceId.ToString());
        command.Parameters.AddWithValue("$batch", remote.BatchId); command.Parameters.AddWithValue("$hash", remote.BatchFingerprint);
        command.Parameters.AddWithValue("$count", remote.BatchEntryCount); command.Parameters.AddWithValue("$ordinal", remote.Ordinal); command.Parameters.AddWithValue("$change", remote.ChangeId);
    }

    private static async Task EnsureNamespaceRemoteRetirementSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using (var definition = connection.CreateCommand())
        {
            definition.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='namespace_member_retirements';";
            var original = await definition.ExecuteScalarAsync(token).ConfigureAwait(false) as string
                ?? throw new InvalidDataException("The original member retirement schema is missing.");
            if (original.Contains("phase IN (0,1,2,3)", StringComparison.Ordinal))
            {
                using var transaction = connection.BeginTransaction();
                await using var migrate = connection.CreateCommand(); migrate.Transaction = transaction;
                migrate.CommandText = """
                    CREATE TABLE namespace_member_retirements_v70 (
                        operation_id TEXT NOT NULL PRIMARY KEY,
                        member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                        payload BLOB NOT NULL,fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                        phase INTEGER NOT NULL CHECK(phase IN (0,1,2,3,4)));
                    INSERT INTO namespace_member_retirements_v70(rowid,operation_id,member_id,payload,fingerprint,phase)
                        SELECT rowid,operation_id,member_id,payload,fingerprint,phase FROM namespace_member_retirements;
                    DROP TABLE namespace_member_retirements;
                    ALTER TABLE namespace_member_retirements_v70 RENAME TO namespace_member_retirements;
                    """;
                await migrate.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                transaction.Commit();
            }
        }
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_retirement_entries(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_retirements(operation_id),
                instance_id TEXT NOT NULL,batch_id TEXT NOT NULL,batch_fingerprint TEXT NOT NULL CHECK(length(batch_fingerprint)=64),
                entry_count INTEGER NOT NULL CHECK(entry_count>0),ordinal INTEGER NOT NULL CHECK(ordinal>=0 AND ordinal<entry_count),change_id TEXT NOT NULL,
                UNIQUE(instance_id,batch_id,ordinal),UNIQUE(instance_id,batch_id,change_id));
            CREATE TABLE IF NOT EXISTS namespace_remote_retirement_outcomes(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_remote_retirement_entries(operation_id),payload BLOB NOT NULL,fingerprint BLOB NOT NULL);
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
