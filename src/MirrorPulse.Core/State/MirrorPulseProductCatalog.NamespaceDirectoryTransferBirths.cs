using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseDirectoryTransferTargetBirth(Guid TransferId, int Ordinal,
    MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthPlan Plan);

public sealed partial class MirrorPulseProductCatalog
{
    private const string DirectoryTransferBirthTable = """
        CREATE TABLE namespace_directory_transfer_target_births(
            transfer_id TEXT NOT NULL REFERENCES namespace_directory_transfer_target_verifications(transfer_id),
            ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 65535),
            birth_id TEXT NOT NULL UNIQUE REFERENCES namespace_birth_intents(operation_id),
            item_id TEXT NOT NULL UNIQUE,
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 262144),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
            PRIMARY KEY(transfer_id,ordinal))
        """;

    private static async Task EnsureDirectoryTransferBirthSchemaAsync(SqliteConnection connection, long originalVersion,
        CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = "SELECT sql FROM sqlite_master WHERE name='namespace_directory_transfer_target_births';";
        var existing = await sql.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null && originalVersion < 112)
        {
            sql.CommandText = DirectoryTransferBirthTable;
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string text ||
            NormalizeRetiredTransferIndexSql(text) != NormalizeRetiredTransferIndexSql(DirectoryTransferBirthTable))
        {
            throw new InvalidDataException("The original directory transfer target birth schema is missing or changed.");
        }
        if (originalVersion < 112)
        {
            sql.CommandText = "SELECT 1 FROM namespace_directory_transfer_target_births LIMIT 1;";
            if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An earlier catalog cannot supply original directory transfer births.");
            sql.CommandText = "PRAGMA user_version=112;";
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    internal async Task<MirrorPulseDirectoryTransferTargetBirth> PrepareNamespaceDirectoryTransferBirthAsync(
        MirrorPulseDirectoryTransferTargetBirth target, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var (admission, verification) = await ReadDirectoryTransferBirthEvidenceAsync(target.TransferId, transaction, token).ConfigureAwait(false);
            ValidateDirectoryTransferBirth(admission, verification, target);
            var retained = await ReadDirectoryTransferBirthRowAsync(target.TransferId, target.Ordinal, null, transaction, token).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained != target) throw new InvalidOperationException("The original directory transfer target birth is immutable.");
                await ValidateDirectoryTransferBirthArtifactsAsync(retained, transaction, token).ConfigureAwait(false);
                transaction.Commit();
                return retained;
            }
            var parent = await ReadDirectoryTransferBirthParentAsync(admission, target.Ordinal, transaction, token).ConfigureAwait(false);
            if (target.Birth.ParentLocalObject != parent.Context.Member.LocalObject ||
                target.Birth.BornParent != parent.BornParent ||
                target.Birth.ParentEvidenceId != (parent.BornParent is null ? parent.Context.Member.OriginId : Guid.Empty) ||
                target.Birth.ParentPermissionOperationId != (parent.BornParent is null ? parent.PermissionOperationId : Guid.Empty))
                throw new InvalidOperationException("A directory transfer birth requires its exact current target parent.");

            await RequireNamespaceTransferRoutesAsync([admission.SourceRoute, admission.TargetRoute], transaction, token,
                requireSingleRoot: true).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(target.Birth.RootId, null, transaction, token,
                allowedTransfer: target.TransferId).ConfigureAwait(false);
            await RequireNamespaceTransferIdAvailableAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false);
            await ValidateNamespaceBirthCurrentParentAsync(target.Birth, transaction, token).ConfigureAwait(false);
            await RequireNamespaceBirthUnoccupiedMemberNameAsync(target.Birth, transaction, token).ConfigureAwait(false);
            await using var sql = _connection.CreateCommand();
            sql.Transaction = transaction;
            sql.CommandText = """
                SELECT 1 FROM namespace_permission_local_identities WHERE item_id=$item
                UNION ALL SELECT 1 FROM namespace_birth_plans WHERE item_id=$item
                UNION ALL SELECT 1 FROM namespace_remote_birth_entries WHERE item_id=$item LIMIT 1;
                """;
            sql.Parameters.AddWithValue("$item", target.Plan.ItemId.ToString("D"));
            if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A directory transfer target cannot adopt an existing public identity.");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(target, TopologyJsonOptions);
            if (payload.Length > 262_144) throw new ArgumentException("The directory transfer birth is not bounded.", nameof(target));
            validation.Dispose();
            await InsertNamespaceTransferBirthArtifactsAsync(target.Birth, target.Plan, transaction, token).ConfigureAwait(false);
            sql.CommandText = """
                INSERT INTO namespace_directory_transfer_target_births(transfer_id,ordinal,birth_id,item_id,payload,fingerprint)
                VALUES($transfer,$ordinal,$birth,$item,$payload,$hash);
                """;
            sql.Parameters.AddWithValue("$transfer", target.TransferId.ToString("D"));
            sql.Parameters.AddWithValue("$ordinal", target.Ordinal);
            sql.Parameters.AddWithValue("$birth", target.Birth.OperationId.ToString("D"));
            sql.Parameters.AddWithValue("$payload", payload);
            sql.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit();
            return target;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferTargetBirth?> ReadNamespaceDirectoryTransferBirthAsync(
        Guid transferId, int ordinal, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var result = await ReadDirectoryTransferBirthRowAsync(transferId, ordinal, null, transaction, token).ConfigureAwait(false);
            if (result is not null)
            {
                var (admission, verification) = await ReadDirectoryTransferBirthEvidenceAsync(transferId, transaction, token).ConfigureAwait(false);
                ValidateRetainedDirectoryTransferBirth(admission, verification, result);
                await ValidateDirectoryTransferBirthArtifactsAsync(result, transaction, token).ConfigureAwait(false);
            }
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot> ReadDirectoryTransferBirthParentAsync(
        MirrorPulseDirectoryTransferAdmission admission, int ordinal, SqliteTransaction transaction, CancellationToken token)
    {
        if (ordinal == 0)
        {
            var original = admission.TargetParent;
            var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(original.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
            if (current != original) throw new InvalidOperationException("The original directory transfer destination parent changed.");
            return original;
        }
        string path = ParentMovePath(admission.Members[ordinal].TargetRelativePath);
        int parentOrdinal = ordinal - 1;
        while (parentOrdinal >= 0 && admission.Members[parentOrdinal].TargetRelativePath != path) parentOrdinal--;
        var parent = await ReadDirectoryTransferBirthRowAsync(admission.Command.CommandId, parentOrdinal, null, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A directory transfer child requires its original parent birth.");
        var snapshot = await ReadNamespaceMemberProtectionSnapshotCoreAsync(parent.Birth.OperationId, transaction, token).ConfigureAwait(false);
        if (snapshot?.BornParent is not { } born || born.BirthOperationId != parent.Birth.OperationId ||
            snapshot.Context.Location.RelativePath != path || snapshot.Context.Location.CurrentPositionOperationId is not null ||
            !snapshot.Context.Member.IsDirectory || snapshot.Context.Member.RootId != admission.TargetRoute.RootId ||
            snapshot.Context.Protection.RoleSid != admission.TargetParent.Context.Protection.RoleSid ||
            snapshot.Identity.ItemId != parent.Plan.ItemId || snapshot.Identity.RemoteId != parent.Plan.RemoteId)
            throw new InvalidOperationException("The directory transfer child requires its original registered protected parent.");
        return snapshot;
    }

    private async Task ValidateCurrentDirectoryTransferBirthRegistrationAsync(MirrorPulseNamespaceBirthIntent birth,
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        var target = await ReadDirectoryTransferBirthRowAsync(null, null, birth.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The directory target registration lost its original owner.");
        var (admission, verification) = await ReadDirectoryTransferBirthEvidenceAsync(target.TransferId, transaction, token).ConfigureAwait(false);
        ValidateRetainedDirectoryTransferBirth(admission, verification, target);
        var parent = await ReadDirectoryTransferBirthParentAsync(admission, target.Ordinal, transaction, token).ConfigureAwait(false);
        if (target.Birth != birth || member.ParentMemberId != parent.Context.Member.MemberId ||
            birth.ParentLocalObject != parent.Context.Member.LocalObject ||
            birth.BornParent != parent.BornParent)
            throw new InvalidOperationException("The directory transfer must register its original birth under its exact current parent.");
        await RequireNamespaceTransferRoutesAsync([admission.SourceRoute, admission.TargetRoute], transaction, token,
            requireSingleRoot: true).ConfigureAwait(false);
    }

    private async Task<(MirrorPulseDirectoryTransferAdmission Admission, MirrorPulseDirectoryTransferTargetVerification Verification)>
        ReadDirectoryTransferBirthEvidenceAsync(Guid transferId, SqliteTransaction transaction, CancellationToken token)
    {
        _namespaceRecoveryReadSnapshots.TryGetValue(transaction, out var snapshot);
        if (snapshot?.DirectoryTransferBirthEvidence.TryGetValue(transferId, out var retained) == true) return retained;
        var copy = await ReadDirectoryTransferCopyCoreAsync(transferId, transaction, token).ConfigureAwait(false);
        var verification = await ReadDirectoryTransferTargetCoreAsync(copy, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Directory transfer target birth requires its original whole target verification.");
        var result = (copy.Admission, verification);
        if (snapshot is not null) snapshot.DirectoryTransferBirthEvidence[transferId] = result;
        return result;
    }

    private static void ValidateDirectoryTransferBirth(MirrorPulseDirectoryTransferAdmission admission,
        MirrorPulseDirectoryTransferTargetVerification verification, MirrorPulseDirectoryTransferTargetBirth target)
    {
        ArgumentNullException.ThrowIfNull(target.Birth);
        ArgumentNullException.ThrowIfNull(target.Plan);
        ValidateNamespaceBirth(target.Birth);
        ValidateNamespaceBirthPlan(target.Plan);
        ValidateNamespaceBirthPlanReference(target.Plan, target.Birth);
        if (target.TransferId != admission.Command.CommandId || target.Ordinal < 0 || target.Ordinal >= admission.Members.Count)
            throw new ArgumentException("The birth must identify an exact directory transfer member.", nameof(target));
        var member = admission.Members[target.Ordinal];
        var verified = verification.Members[target.Ordinal];
        if (target.Birth.TransferEntry != MirrorPulseNamespaceTransferBirthReference.Create(verification, member.CopyOperationId) ||
            target.Birth.RootId != admission.TargetRoute.RootId || target.Birth.RelativePath != member.TargetRelativePath ||
            target.Birth.IsDirectory != verified.IsDirectory || target.Birth.RoleSid != admission.TargetParent.Context.Protection.RoleSid ||
            target.Birth.PreparedAt < verification.VerifiedAt || target.Plan.PreparedAt != target.Birth.PreparedAt ||
            target.Plan.RemoteRevision != verified.Revision || target.Plan.Content != verified.Content ||
            admission.Members.Any(source => source.Source.Protection.Identity.ItemId == target.Plan.ItemId))
            throw new ArgumentException("The birth must retain the verified member, destination, role and a separate public identity.", nameof(target));
    }

    private static void ValidateRetainedDirectoryTransferBirth(MirrorPulseDirectoryTransferAdmission admission,
        MirrorPulseDirectoryTransferTargetVerification verification, MirrorPulseDirectoryTransferTargetBirth target)
    {
        try { ValidateDirectoryTransferBirth(admission, verification, target); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("The original directory transfer target birth is inconsistent.", error); }
    }

    private async Task<MirrorPulseDirectoryTransferTargetBirth?> ReadDirectoryTransferBirthRowAsync(Guid? transferId, int? ordinal,
        Guid? birthId, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = birthId.HasValue
            ? "SELECT transfer_id,ordinal,birth_id,item_id,payload,fingerprint FROM namespace_directory_transfer_target_births WHERE birth_id=$id;"
            : "SELECT transfer_id,ordinal,birth_id,item_id,payload,fingerprint FROM namespace_directory_transfer_target_births WHERE transfer_id=$id AND ordinal=$ordinal;";
        sql.Parameters.AddWithValue("$id", (birthId ?? transferId!.Value).ToString("D"));
        if (!birthId.HasValue) sql.Parameters.AddWithValue("$ordinal", ordinal!.Value);
        await using var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var row = ReadNamespaceMovePayload<MirrorPulseDirectoryTransferTargetBirth>((byte[])reader[4], (byte[])reader[5],
            262_144, value =>
            {
                if (value.Birth is null || value.Plan is null || value.Birth.TransferEntry?.Version != 2)
                    throw new ArgumentException("The directory transfer target owner is incomplete.");
                ValidateNamespaceBirth(value.Birth);
                ValidateNamespaceBirthPlan(value.Plan);
                ValidateNamespaceBirthPlanReference(value.Plan, value.Birth);
            });
        if (reader.GetString(0) != row.TransferId.ToString("D") || reader.GetInt64(1) != row.Ordinal ||
            reader.GetString(2) != row.Birth.OperationId.ToString("D") || reader.GetString(3) != row.Plan.ItemId.ToString("D"))
            throw new InvalidDataException("The directory transfer target birth index changed.");
        return row;
    }

    private async Task ValidateDirectoryTransferBirthArtifactsAsync(MirrorPulseDirectoryTransferTargetBirth target,
        SqliteTransaction? transaction, CancellationToken token)
    {
        if (await ReadNamespaceBirthRowAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false) != target.Birth ||
            await ReadNamespaceBirthPlanRowAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false) != target.Plan)
            throw new InvalidDataException("The directory transfer birth lost its immutable admission or public identity plan.");
    }

    private async Task ValidateDirectoryTransferBirthReferenceIndexAsync(MirrorPulseNamespaceBirthIntent birth,
        SqliteTransaction? transaction, CancellationToken token)
    {
        using var owned = transaction is null ? BeginNamespaceRecoveryReadSnapshot() : null;
        var snapshot = transaction ?? owned!;
        var target = await ReadDirectoryTransferBirthRowAsync(null, null, birth.OperationId, snapshot, token).ConfigureAwait(false);
        if (target is null || target.Birth != birth)
            throw new InvalidDataException("The birth lost its original directory transfer owner.");
        try
        {
            var (admission, verification) = await ReadDirectoryTransferBirthEvidenceAsync(target.TransferId, snapshot, token).ConfigureAwait(false);
            ValidateRetainedDirectoryTransferBirth(admission, verification, target);
            if (await ReadNamespaceBirthPlanRowAsync(birth.OperationId, snapshot, token).ConfigureAwait(false) != target.Plan)
                throw new InvalidDataException("The directory transfer birth lost its immutable public identity plan.");
        }
        catch (Exception error) when (error is FileNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("The directory transfer birth lost its original verified target evidence.", error); }
        owned?.Commit();
    }
}
