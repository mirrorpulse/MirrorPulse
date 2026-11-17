using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseDirectoryTransferStartRecovery(Guid BirthId, DateTimeOffset StartedAt,
    MirrorPulseDirectoryTransferTargetActivation Activation, DateTimeOffset AdmittedAt);

public sealed partial class MirrorPulseProductCatalog
{
    private const string DirectoryTransferStartRecoveryTable = """
        CREATE TABLE namespace_directory_transfer_start_recoveries(
            birth_id TEXT PRIMARY KEY NOT NULL REFERENCES namespace_directory_transfer_target_births(birth_id),
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 4096),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
        """;

    private static async Task EnsureDirectoryTransferStartRecoverySchemaAsync(SqliteConnection connection, long version, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT sql FROM sqlite_master WHERE name='namespace_directory_transfer_start_recoveries';";
        var existing = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null && version < 120)
        {
            query.CommandText = DirectoryTransferStartRecoveryTable;
            await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string sql ||
            NormalizeRetiredTransferIndexSql(sql) != NormalizeRetiredTransferIndexSql(DirectoryTransferStartRecoveryTable))
            throw new InvalidDataException("The directory transfer start recovery schema is missing or changed.");
        if (version < 120)
        {
            query.CommandText = "SELECT 1 FROM namespace_directory_transfer_start_recoveries LIMIT 1;";
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An earlier catalog cannot supply directory transfer start recovery records.");
            query.CommandText = "PRAGMA user_version=120;";
            await query.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    internal async Task RecordDirectoryTransferStartRecoveryAsync(Guid transferId, Guid birthId,
        MirrorPulseDirectoryTransferTargetActivation activation, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var (admission, verified) = await ReadDirectoryTransferBirthEvidenceAsync(transferId, transaction, token).ConfigureAwait(false);
            var target = await ReadDirectoryTransferBirthRowAsync(null, null, birthId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The directory recovery lost its selected birth.");
            ValidateRetainedDirectoryTransferBirth(admission, verified, target);
            if (await ReadDirectoryTransferSourceDeletionCoreAsync(transferId, transaction, token).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("A directory recovery cannot replace a published target.");
            await RequireNamespaceTransferRoutesAsync([admission.SourceRoute, admission.TargetRoute], transaction, token, requireSingleRoot: true)
                .ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(target.Birth.RootId, null, transaction, token, allowedTransfer: transferId)
                .ConfigureAwait(false);
            var admittedAt = DateTimeOffset.UtcNow;
            await RequireRegisteredDirectoryTransferTargetPrefixAsync(transferId, admittedAt, transaction, token, birthId).ConfigureAwait(false);
            var start = (await ReadNamespaceBirthStartRowAsync(birthId, transaction, token).ConfigureAwait(false))!;
            var recovery = new MirrorPulseDirectoryTransferStartRecovery(birthId, start.StartedAt, activation, admittedAt);
            ValidateDirectoryTransferStartRecovery(recovery);
            var audit = await ValidateDirectoryTransferStartRecoveryAuditsAsync(admission, target, start, recovery, transaction, token)
                .ConfigureAwait(false);
            var parent = await ReadDirectoryTransferBirthParentAsync(admission, target, transaction, token, activation.RoleSid).ConfigureAwait(false);
            if (parent.Context.Member.LocalObject != target.Birth.ParentLocalObject || parent.Context.Protection.VerifiedAt > audit.CompletedAt)
                throw new InvalidOperationException("The directory recovery requires its original audited target parent.");
            var claim = new NamespaceBirthParentClaim(parent.BornParent is null ? parent.PermissionOperationId : Guid.Empty,
                parent.BornParent, activation.RoleSid, admittedAt);
            _ = await ValidateNamespaceBirthLineageAsync(target.Birth, null, true, claim, transaction, token).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(recovery, TopologyJsonOptions);
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO namespace_directory_transfer_start_recoveries(birth_id,payload,fingerprint) VALUES($id,$payload,$hash)
                ON CONFLICT(birth_id) DO UPDATE SET payload=$payload,fingerprint=$hash;
                """;
            command.Parameters.AddWithValue("$id", birthId.ToString("D"));
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> ReadDirectoryTransferCreationRoleAsync(Guid birthId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var target = await ReadDirectoryTransferBirthRowAsync(null, null, birthId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The directory creation lost its original selected target.");
            var (admission, verified) = await ReadDirectoryTransferBirthEvidenceAsync(target.TransferId, transaction, token).ConfigureAwait(false);
            ValidateRetainedDirectoryTransferBirth(admission, verified, target);
            var start = await ReadNamespaceBirthStartRowAsync(birthId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The directory creation lost its original start.");
            string role = await ReadDirectoryTransferCreationRoleCoreAsync(admission, target, start, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return role;
        }
        finally { _gate.Release(); }
    }

    private async Task<string> ReadDirectoryTransferCreationRoleCoreAsync(MirrorPulseDirectoryTransferAdmission admission,
        MirrorPulseDirectoryTransferTargetBirth target, MirrorPulseNamespaceBirthStart start, SqliteTransaction transaction, CancellationToken token)
    {
        if (start.DirectoryTransferActivation is { } originalActivation)
            await RequireDirectoryTransferActivationAuditsAsync(admission, target, start, originalActivation, transaction, token).ConfigureAwait(false);
        MirrorPulseDirectoryTransferStartRecovery? recovery = null;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT payload,fingerprint FROM namespace_directory_transfer_start_recoveries WHERE birth_id=$id;";
            query.Parameters.AddWithValue("$id", start.OperationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                recovery = ReadNamespaceMovePayload<MirrorPulseDirectoryTransferStartRecovery>((byte[])reader[0], (byte[])reader[1],
                    4096, ValidateDirectoryTransferStartRecovery);
        }
        if (recovery is null) return start.DirectoryTransferActivation?.RoleSid ?? target.Birth.RoleSid;
        await ValidateDirectoryTransferStartRecoveryAuditsAsync(admission, target, start, recovery, transaction, token).ConfigureAwait(false);
        return recovery.Activation.RoleSid;
    }

    private static void ValidateDirectoryTransferStartRecovery(MirrorPulseDirectoryTransferStartRecovery recovery)
    {
        if (recovery.BirthId == Guid.Empty || recovery.StartedAt == default || recovery.AdmittedAt < recovery.StartedAt ||
            recovery.Activation is not { } activation || activation.SourceAuditId == Guid.Empty || activation.TargetAuditId == Guid.Empty ||
            activation.SourceAuditId == activation.TargetAuditId)
            throw new ArgumentException("Directory recovery requires its original start and two root audits.", nameof(recovery));
        ValidatePermissionSid(activation.RoleSid, role: true);
    }

    private async Task<MirrorPulseNamespaceInitializationCompletion> ValidateDirectoryTransferStartRecoveryAuditsAsync(
        MirrorPulseDirectoryTransferAdmission admission, MirrorPulseDirectoryTransferTargetBirth target, MirrorPulseNamespaceBirthStart start,
        MirrorPulseDirectoryTransferStartRecovery recovery, SqliteTransaction transaction, CancellationToken token)
    {
        if (start.Version != 2 || recovery.BirthId != start.OperationId || recovery.BirthId != target.Birth.OperationId ||
            recovery.StartedAt != start.StartedAt || recovery.Activation.RoleSid == (start.DirectoryTransferActivation?.RoleSid ?? target.Birth.RoleSid))
            throw new InvalidOperationException("Directory recovery requires the original start and a new audited role.");
        var source = await RequireAuditAsync(recovery.Activation.SourceAuditId, admission.SourceRoute.RootId).ConfigureAwait(false);
        var destination = await RequireAuditAsync(recovery.Activation.TargetAuditId, admission.TargetRoute.RootId).ConfigureAwait(false);
        if (source.AnchorMemberId != destination.AnchorMemberId)
            throw new InvalidOperationException("Directory recovery requires both original root audits under the same anchor.");
        return destination;

        async Task<MirrorPulseNamespaceInitializationCompletion> RequireAuditAsync(Guid auditId, RootId rootId)
        {
            var status = await ReadNamespaceInitializationStatusCoreAsync(auditId, transaction, token).ConfigureAwait(false);
            if (status.Intent.DirectoryTransferOperationId != target.TransferId || status.Intent.DirectoryTransferStartedBirthId != recovery.BirthId ||
                status.Intent.PreparedAt < start.StartedAt || status.Completion is not { } completed || completed.ScopeRoot != rootId ||
                completed.RoleSid != recovery.Activation.RoleSid || completed.CompletedAt > recovery.AdmittedAt)
                throw new InvalidOperationException("Directory recovery requires the original completed root audit for the started target.");
            return completed;
        }
    }
}
