using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>The Host's final guarded source observation of one original sealed member.</summary>
public sealed record MirrorPulseRootLabelCancellationMember(Guid MemberId, Guid ProtectionId,
    MirrorPulseNamespacePermissionVerification Native);

/// <summary>Retains a public NotMoved result and the complete guarded original source before explicit cancellation.</summary>
/// <remarks>This grants no native execution, current readiness, permission rotation, source mutation or journal ACK.</remarks>
public sealed record MirrorPulseRootLabelCancellation(int Version, Guid OperationId, RootId RootId,
    string PublicEvidenceSha256, DateTimeOffset PublicNotMovedAt, DateTimeOffset VerifiedAt,
    bool NativeMoveObserved, bool DurableProjectionCommitted, bool RequiresFullRescan, int DurableStateEntriesUpdated,
    Guid AnchorMemberId, Guid AnchorProtectionId, MirrorPulseNamespacePermissionVerification Anchor,
    IReadOnlyList<MirrorPulseRootLabelCancellationMember> Members);

public sealed partial class MirrorPulseProductCatalog
{
    private const int RootLabelCancellationPayloadLimit = 16 * 1024 * 1024;

    /// <summary>Atomically cancels an unstarted sealed Label and releases only its own namespace ownership.</summary>
    /// <remarks>The Host must retain root/instance/library/actor and complete native source/anchor guards through this call.
    /// No-start is checked again in this transaction. A historical cancellation grants no new admission.</remarks>
    public async Task<MirrorPulseRootLabelCancellation> CancelUnstartedRootLabelAsync(MirrorPulseRootLabelCancellation cancellation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ValidateRootLabelCancellation(cancellation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var intent = await ReadNamespaceMemberMoveCoreAsync(cancellation.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original protected Label admission is missing.");
            var replay = await ReadRootLabelCancellationRowAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!JsonSerializer.SerializeToUtf8Bytes(replay, TopologyJsonOptions).AsSpan().SequenceEqual(
                    JsonSerializer.SerializeToUtf8Bytes(cancellation, TopologyJsonOptions)))
                    throw new InvalidOperationException("The original guarded cancellation is immutable.");
                await ValidateRootLabelCancellationReferencesAsync(intent, replay, current: false, transaction, cancellationToken).ConfigureAwait(false);
                transaction.Commit(); return replay;
            }
            _ = await RequireUnstartedRootLabelCancellationAdmissionCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateRootLabelCancellationReferencesAsync(intent, cancellation, current: true, transaction, cancellationToken).ConfigureAwait(false);
            var history = await ReadManagedRootRenameHistoryCoreAsync(intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
            var current = (await ReadManagedRootRenamesCoreAsync(cancellationToken, transaction).ConfigureAwait(false))
                .SingleOrDefault(item => item.RootId == intent.RootId);
            if (current != history.Intent || current?.Phase != MirrorPulseRootRenamePhase.Prepared)
                throw new InvalidOperationException("The cancellation must retain the current original unmoved Label.");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(cancellation, TopologyJsonOptions);
            if (payload.Length > RootLabelCancellationPayloadLimit) throw new ArgumentException("The complete cancellation must remain bounded.", nameof(cancellation));
            await using (var write = _connection.CreateCommand())
            {
                write.Transaction = transaction;
                write.CommandText = """
                    INSERT INTO root_label_unstarted_cancellations(operation_id,root_id,payload,fingerprint)
                    VALUES($operation,$root,$payload,$hash);
                    DELETE FROM namespace_member_move_pending WHERE root_id=$root AND operation_id=$operation;
                    DELETE FROM namespace_member_move_reservations WHERE operation_id=$operation;
                    DELETE FROM namespace_member_move_predecessor_claims WHERE operation_id=$operation;
                    """;
                write.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
                write.Parameters.AddWithValue("$root", intent.RootId.ToString());
                write.Parameters.AddWithValue("$payload", payload); write.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await SaveManagedRootRenameCoreAsync(history.Intent with
            { Phase = MirrorPulseRootRenamePhase.Cancelled, UpdatedAt = cancellation.VerifiedAt, CancellationOperationId = intent.OperationId },
                cancellationToken, transaction).ConfigureAwait(false);
            await ValidateRootLabelCancellationReferencesAsync(intent, cancellation, current: false, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateNamespaceMovePredecessorClaimAsync(intent, null, cancelled: true, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return cancellation;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRootLabelCancellation?> ReadRootLabelCancellationAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var result = await ReadRootLabelCancellationRowAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                if (intent is null) throw new InvalidDataException("The cancellation lost its original sealed Label admission.");
                await ValidateRootLabelCancellationReferencesAsync(intent, result, current: false, transaction, cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateRootLabelCancellation(MirrorPulseRootLabelCancellation value)
    {
        if (value.Version != 1 || value.OperationId == Guid.Empty || value.RootId.Value == Guid.Empty ||
            value.PublicEvidenceSha256 is null || value.PublicEvidenceSha256.Length != 64 ||
            value.PublicEvidenceSha256.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')) ||
            value.PublicNotMovedAt == default || value.VerifiedAt < value.PublicNotMovedAt || value.NativeMoveObserved ||
            value.DurableProjectionCommitted || value.RequiresFullRescan || value.DurableStateEntriesUpdated != 0 ||
            value.AnchorMemberId == Guid.Empty || value.AnchorProtectionId == Guid.Empty || value.Members is null || value.Members.Count is < 1 or > 65_536 ||
            value.Members.Any(member => member is null || member.MemberId == Guid.Empty || member.ProtectionId == Guid.Empty) ||
            value.Members.Select(member => member.MemberId).Distinct().Count() != value.Members.Count)
            throw new ArgumentException("Cancellation requires its complete bounded original source and an actual public NotMoved result before any start.");
        foreach (var native in value.Members.Select(member => member.Native).Prepend(value.Anchor))
        {
            ArgumentNullException.ThrowIfNull(native);
            ValidatePermissionBinding(native.LocalObject); ValidatePermissionSid(native.OwnerSid, false); ValidatePermissionDacl(native.Dacl);
            if (native.ObservedAt < value.PublicNotMovedAt || native.ObservedAt > value.VerifiedAt)
                throw new ArgumentException("Every final guarded native observation must follow public reconciliation and precede cancellation.");
        }
    }
}
