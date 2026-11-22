using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Fresh evidence that an original remote move remained at its source with a durable SDK conflict.</summary>
/// <remarks>This releases only the rejected admission. It is not a move completion, conflict resolution or content acknowledgement.</remarks>
public sealed record MirrorPulseNamespaceRemoteMoveRejection(int Version, Guid OperationId, Guid ConflictId,
    DateTimeOffset ConflictDetectedAt, DateTimeOffset VerifiedAt, string? NativeRemoteRevision, string? OfficialRemoteRevision,
    MirrorPulseNamespacePermissionVerification Child, MirrorPulseNamespacePermissionVerification SourceParent,
    MirrorPulseNamespacePermissionVerification TargetParent);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains an unchanged source and releases its own pending move and target reservation atomically.</summary>
    public async Task<MirrorPulseNamespaceRemoteMoveRejection> RecordNamespaceRemoteMoveRejectionAsync(
        MirrorPulseNamespaceRemoteMoveRejection rejection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        ValidateNamespaceRemoteMoveRejection(rejection);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var intent = await ReadNamespaceMemberMoveCoreAsync(rejection.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original remote move admission is missing.");
            var replay = await ReadNamespaceRemoteMoveRejectionRowAsync(rejection.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != rejection) throw new InvalidOperationException("A retained remote move rejection is immutable.");
                transaction.Commit(); return replay;
            }
            _ = await RequireNamespaceMemberMoveExecutionCoreAsync(rejection.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateNamespaceRemoteMoveRejectionReferencesAsync(intent, rejection, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(rejection, TopologyJsonOptions);
            if (payload.Length > NamespaceMemberMoveFactLimit) throw new ArgumentException("Remote rejection evidence must be bounded.", nameof(rejection));
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_remote_move_rejections(operation_id,conflict_id,payload,fingerprint)
                VALUES($operation,$conflict,$payload,$hash);
                DELETE FROM namespace_member_move_pending WHERE root_id=$root AND operation_id=$operation;
                DELETE FROM namespace_member_move_reservations WHERE operation_id=$operation;
                DELETE FROM namespace_member_move_predecessor_claims WHERE operation_id=$operation;
                """;
            insert.Parameters.AddWithValue("$operation", rejection.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$conflict", rejection.ConflictId.ToString("D"));
            insert.Parameters.AddWithValue("$root", intent.RootId.ToString());
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return rejection;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteMoveRejection?> ReadNamespaceRemoteMoveRejectionAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A remote move ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var result = await ReadNamespaceRemoteMoveRejectionRowAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (result is not null && intent is null) throw new InvalidDataException("The remote rejection lost its original admission.");
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceRemoteMoveRejection?> ReadNamespaceRemoteMoveRejectionRowAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,conflict_id,payload,fingerprint FROM namespace_remote_move_rejections WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var result = ReadNamespaceMovePayload<MirrorPulseNamespaceRemoteMoveRejection>((byte[])reader[2], (byte[])reader[3],
            NamespaceMemberMoveFactLimit, ValidateNamespaceRemoteMoveRejection);
        if (result.OperationId != operationId || reader.GetString(0) != operationId.ToString("D") || reader.GetString(1) != result.ConflictId.ToString("D"))
            throw new InvalidDataException("The remote rejection index disagrees with its original evidence.");
        return result;
    }

    private static void ValidateNamespaceRemoteMoveRejection(MirrorPulseNamespaceRemoteMoveRejection rejection)
    {
        if (rejection.Version != 1 || rejection.OperationId == Guid.Empty || rejection.ConflictId == Guid.Empty ||
            rejection.ConflictDetectedAt == default || rejection.VerifiedAt < rejection.ConflictDetectedAt ||
            new[] { rejection.NativeRemoteRevision, rejection.OfficialRemoteRevision }.Any(value => value is not null && (value.Length > 16_384 || value.Contains('\0'))))
            throw new ArgumentException("A remote rejection requires its original conflict and fresh bounded observations.", nameof(rejection));
        foreach (var observed in new[] { rejection.Child, rejection.SourceParent, rejection.TargetParent })
        {
            ArgumentNullException.ThrowIfNull(observed);
            ValidatePermissionBinding(observed.LocalObject); ValidatePermissionSid(observed.OwnerSid, false); ValidatePermissionDacl(observed.Dacl);
            if (observed.ObservedAt == default || observed.ObservedAt > rejection.VerifiedAt)
                throw new ArgumentException("Remote rejection observations must precede their final verification.", nameof(rejection));
        }
    }

    private async Task ValidateNamespaceRemoteMoveRejectionReferencesAsync(MirrorPulseNamespaceMemberMoveIntent intent,
        MirrorPulseNamespaceRemoteMoveRejection rejection, SqliteTransaction transaction, CancellationToken token)
    {
        var start = await ReadNamespaceMemberMoveStartCoreAsync(intent.OperationId, transaction, token, false).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Remote rejection cannot invent a before-native start.");
        string? originalOfficialRevision = start.Version == 2 ? start.OriginalOfficialRemoteRevision : intent.RemoteEntry?.PreviousRemoteRevision;
        if (intent.RemoteEntry is not { } remote || rejection.OperationId != intent.OperationId || rejection.ConflictDetectedAt < start.StartedAt ||
            rejection.NativeRemoteRevision != remote.OriginalRemoteRevision || rejection.OfficialRemoteRevision != originalOfficialRevision)
            throw new InvalidOperationException("Remote rejection must retain the original start, native identity and unmoved official revision.");
        if (intent.DirectoryMoveEvidence is not null &&
            await ReadNamespaceDirectoryMoveSnapshotCoreAsync(intent.OperationId, transaction, token).ConfigureAwait(false) is null)
            throw new InvalidDataException("The rejected remote directory lost its original sealed subtree.");
        foreach (var entry in new[] { (Id: intent.MemberId, Protection: intent.ProtectionId, Binding: intent.LocalObject, Observed: rejection.Child),
            (Id: intent.Source.ParentMemberId, Protection: intent.Source.ParentProtectionId, Binding: intent.Source.ParentLocalObject, Observed: rejection.SourceParent),
            (Id: intent.Target.ParentMemberId, Protection: intent.Target.ParentProtectionId, Binding: intent.Target.ParentLocalObject, Observed: rejection.TargetParent) })
        {
            var member = await ReadNamespaceMemberCoreAsync(entry.Id, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The rejected move lost its original member or parent.");
            var proof = await ReadNamespaceMoveRetainedVerificationAsync(member, entry.Protection, transaction, token).ConfigureAwait(false);
            if (entry.Observed.LocalObject != entry.Binding || entry.Observed.OwnerSid != intent.OwnerSid ||
                entry.Observed.Dacl != proof.Dacl || entry.Observed.ObservedAt < rejection.ConflictDetectedAt)
                throw new InvalidOperationException("Remote rejection requires fresh unchanged original object and parent protections.");
        }
        await using var check = _connection.CreateCommand(); check.Transaction = transaction;
        check.CommandText = """
            SELECT 1 FROM namespace_member_move_observations WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_projections WHERE operation_id=$operation
            UNION ALL SELECT 1 FROM namespace_member_move_completions WHERE operation_id=$operation
            LIMIT 1;
            """;
        check.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
        if (await check.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A move with native or successful projection evidence cannot be rejected as unmoved.");
    }
}
