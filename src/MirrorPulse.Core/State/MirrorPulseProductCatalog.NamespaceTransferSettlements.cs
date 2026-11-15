using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public enum MirrorPulseTransferAcknowledgementKind { OriginalJournal, CommandWithoutCapturedNotifications }

/// <summary>The durable first ACK invocation and its correlated successful return, not a queue-absence inference.</summary>
/// <remarks>Original IDs are the transfer's immutable JournalClaim. Source ACK always uses a null revision.
/// An empty captured set settles only the explicit command and cannot claim a journal acknowledgement.</remarks>
public sealed record MirrorPulseNamespaceTransferAcknowledgement(int Version, Guid OperationId,
    MirrorPulseTransferAcknowledgementKind Kind, DateTimeOffset SourceNameSettledAt, Guid TargetProtectionId,
    MirrorPulseTransferTargetVerification TargetAtStart, DateTimeOffset StartedAt, DateTimeOffset? AcknowledgedAt = null)
{
    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal MirrorPulseFileTransferRecovery? PublishedRecovery { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains original ACK ownership before the public idempotent API, leaving both roots fenced.</summary>
    /// <remarks>The Host must retain actor, both root admissions, native parents/target, private input and
    /// the actual original public tombstone. A finite scan or this transaction does not drain the watcher.</remarks>
    public Task<MirrorPulseNamespaceTransferCopy> StartNamespaceTransferAcknowledgementAsync(Guid operationId,
        MirrorPulseTransferTargetVerification freshTarget, Guid targetProtectionId, DateTimeOffset startedAt,
        CancellationToken token = default) =>
        StartNamespaceTransferAcknowledgementAsync(operationId, freshTarget, targetProtectionId, startedAt, null, token);

    internal async Task<MirrorPulseNamespaceTransferCopy> StartNamespaceTransferAcknowledgementAsync(Guid operationId,
        MirrorPulseTransferTargetVerification freshTarget, Guid targetProtectionId, DateTimeOffset startedAt,
        MirrorPulseFileTransferRecovery? recovery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(freshTarget);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            if (copy.Acknowledgement?.AcknowledgedAt is not null) { transaction.Commit(); return copy; }
            var retirement = await ReadNamespaceRetirementCoreAsync(copy.Plan.DeleteOperationId, transaction, token, copy).ConfigureAwait(false);
            if (retirement is not { Phase: MirrorPulseNamespaceRetirementPhase.Completed, CompletedAt: { } settled, TransferReceipt: not null })
                throw new InvalidOperationException("Original ACK requires the completed owned source name settlement and native receipt.");
            var acknowledgement = new MirrorPulseNamespaceTransferAcknowledgement(recovery is null ? 1 : 2, operationId,
                copy.JournalClaim?.Members.Count == 0 ? MirrorPulseTransferAcknowledgementKind.CommandWithoutCapturedNotifications :
                    MirrorPulseTransferAcknowledgementKind.OriginalJournal, settled, targetProtectionId, freshTarget, startedAt)
            {
                PublishedRecovery = recovery
            };
            ValidateNamespaceTransferAcknowledgement(copy, acknowledgement);
            await RequireCurrentTransferSettlementAsync(copy, targetProtectionId, recovery, transaction, token).ConfigureAwait(false);
            // A crash after SDK ACK must replay the SAME first start and original claim, never replace them.
            if (copy.Acknowledgement is null)
            {
                copy = copy with { Acknowledgement = acknowledgement };
                await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            }
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically saves the successful original ACK return and removes exactly both owned root fences.</summary>
    /// <remarks>Call only after the actual public API returns for the retained claim, or for its explicit empty
    /// command outcome. Never use queue absence. After SDK success the Host completes with CancellationToken.None.
    /// Completed replay reads history without requiring live routes, Worker calls or current native protection.</remarks>
    public Task<MirrorPulseNamespaceTransferCopy> CompleteNamespaceTransferAcknowledgementAsync(
        MirrorPulseNamespaceTransferAcknowledgement originalStart, DateTimeOffset acknowledgedAt, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(originalStart);
        return CompleteNamespaceTransferAcknowledgementAsync(originalStart, acknowledgedAt, originalStart.TargetProtectionId, null, token);
    }

    internal async Task<MirrorPulseNamespaceTransferCopy> CompleteNamespaceTransferAcknowledgementAsync(
        MirrorPulseNamespaceTransferAcknowledgement originalStart, DateTimeOffset acknowledgedAt, Guid currentTargetProtectionId,
        MirrorPulseFileTransferRecovery? recovery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(originalStart);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(originalStart.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            var retained = copy.Acknowledgement ?? throw new InvalidOperationException("ACK cannot complete without its original durable start.");
            if (originalStart.AcknowledgedAt is not null || retained with { AcknowledgedAt = null } != originalStart)
                throw new InvalidOperationException("ACK completion must retain the same first invocation and original ownership.");
            if (retained.AcknowledgedAt is not null) { transaction.Commit(); return copy; }
            if (acknowledgedAt < retained.StartedAt) throw new ArgumentException("ACK return cannot predate its first invocation.", nameof(acknowledgedAt));
            if (recovery is null && currentTargetProtectionId != retained.TargetProtectionId)
                throw new InvalidOperationException("A different acknowledgement protection requires fresh original-root audits.");
            await RequireCurrentTransferSettlementAsync(copy, currentTargetProtectionId, recovery, transaction, token).ConfigureAwait(false);
            copy = copy with { Acknowledgement = retained with { AcknowledgedAt = acknowledgedAt } };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            await using var remove = _connection.CreateCommand(); remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM namespace_transfer_roots WHERE operation_id=$id AND root_id IN ($source,$target);";
            remove.Parameters.AddWithValue("$id", copy.Plan.OperationId.ToString("D"));
            remove.Parameters.AddWithValue("$source", copy.Plan.SourceRoute.RootId.ToString());
            remove.Parameters.AddWithValue("$target", copy.Plan.TargetRoute.RootId.ToString());
            if (await remove.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 2)
                throw new InvalidDataException("ACK completion lost one of its two original root fences.");
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateNamespaceTransferAcknowledgement(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceTransferAcknowledgement acknowledgement)
    {
        if (acknowledgement.Version is not (1 or 2) || (acknowledgement.Version == 2) != (acknowledgement.PublishedRecovery is not null) ||
            acknowledgement.OperationId != copy.Plan.OperationId ||
            copy.Plan.Version != 2 || copy.Plan.CommandOrigin is null || copy.JournalClaim is null ||
            copy.SourceDeletion?.AcceptedAt is not { } deleted || copy.TargetPublication is null ||
            acknowledgement.TargetProtectionId == Guid.Empty || acknowledgement.TargetAtStart is null ||
            acknowledgement.Kind != (copy.JournalClaim.Members.Count == 0 ? MirrorPulseTransferAcknowledgementKind.CommandWithoutCapturedNotifications :
                MirrorPulseTransferAcknowledgementKind.OriginalJournal) || acknowledgement.SourceNameSettledAt < deleted ||
            acknowledgement.TargetAtStart.VerifiedAt < acknowledgement.SourceNameSettledAt ||
            acknowledgement.StartedAt < acknowledgement.TargetAtStart.VerifiedAt ||
            acknowledgement.AcknowledgedAt is { } returned && returned < acknowledgement.StartedAt)
            throw new ArgumentException("Original ACK must retain the explicit command, original captured set, settled source, target and chronology.");
        ValidateNamespaceTransferTargetVerification(copy, acknowledgement.TargetAtStart);
    }

    private async Task ValidateNamespaceTransferAcknowledgementReferencesAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (copy.Acknowledgement is not { } acknowledgement) return;
        var retirement = await ReadNamespaceRetirementCoreAsync(copy.Plan.DeleteOperationId, transaction, token, copy).ConfigureAwait(false);
        var protection = await ReadNamespaceBirthProtectionCoreAsync(acknowledgement.TargetProtectionId, transaction, token).ConfigureAwait(false);
        if (retirement is not { Phase: MirrorPulseNamespaceRetirementPhase.Completed, TransferReceipt: not null } ||
            retirement.CompletedAt != acknowledgement.SourceNameSettledAt || retirement.Intent.TransferEntry?.OperationId != copy.Plan.OperationId ||
            protection is null || protection.BirthOperationId != copy.TargetBirth!.Birth.OperationId ||
            protection.Verification.LocalObject != copy.TargetPublication!.LocalObject ||
            protection.RoleSid != (acknowledgement.PublishedRecovery?.RoleSid ?? retirement.Intent.RoleSid) ||
            protection.Verification.ObservedAt > acknowledgement.StartedAt)
            throw new InvalidDataException("Original ACK lost its completed source receipt or historical target protection.");
        if (acknowledgement.PublishedRecovery is { } recovery)
        {
            var (sourceAudit, targetAudit) = await RequireFileTransferAuditsAsync(copy.Plan, recovery,
                copy.TargetPublication!.PublishedAt, transaction, token).ConfigureAwait(false);
            if (sourceAudit.CompletedAt > acknowledgement.StartedAt || targetAudit.CompletedAt > acknowledgement.StartedAt ||
                protection.Verification.ObservedAt > targetAudit.CompletedAt)
                throw new InvalidDataException("Original ACK lost its audited parent and target lineage.");
        }
    }

    private async Task RequireCurrentTransferSettlementAsync(MirrorPulseNamespaceTransferCopy copy, Guid targetProtectionId,
        MirrorPulseFileTransferRecovery? recovery, SqliteTransaction transaction, CancellationToken token)
    {
        await RequireNamespaceTransferRoutesAsync([copy.Plan.SourceRoute, copy.Plan.TargetRoute], transaction, token).ConfigureAwait(false);
        var retirement = await ReadNamespaceRetirementCoreAsync(copy.Plan.DeleteOperationId, transaction, token, copy).ConfigureAwait(false)
            ?? throw new InvalidDataException("Original ACK lost its source retirement.");
        if (recovery is null)
        {
            foreach (var parent in new[] { retirement.Intent.TransferEntry!.RecoveredSourceParent ?? copy.Plan.SourceParent,
                retirement.Intent.TransferEntry.RecoveredTargetParent ?? copy.ActiveTargetParent })
                if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != parent)
                    throw new InvalidOperationException("Unfinished transfer settlement requires its exact current parents and execution role.");
        }
        else
            _ = await ReadAuditedFileTransferSettlementParentsAsync(copy, retirement, recovery, transaction, token).ConfigureAwait(false);
        var target = await ReadNamespaceMemberProtectionSnapshotCoreAsync(copy.TargetPublication!.MemberId, transaction, token).ConfigureAwait(false);
        if (target is null || target.Context.Protection.ProtectionId != targetProtectionId ||
            target.Context.Protection.RoleSid != (recovery?.RoleSid ?? retirement.Intent.RoleSid) ||
            target.Context.Member.LocalObject != copy.TargetPublication.LocalObject || target.Identity.ItemId != copy.TargetPublication.ItemId ||
            target.Identity.RemoteId != copy.TargetBirth!.Plan.RemoteId || target.Context.Location.RelativePath != copy.Plan.TargetRelativePath ||
            target.Context.Location.ParentMemberId != copy.Plan.TargetParent.Context.Member.MemberId)
            throw new InvalidOperationException("Original ACK requires the unchanged current protected target and its original position.");
        await using var names = _connection.CreateCommand(); names.Transaction = transaction;
        names.CommandText = "SELECT 1 FROM namespace_member_names WHERE member_id=$source LIMIT 1;";
        names.Parameters.AddWithValue("$source", copy.Plan.Source.Context.Member.MemberId.ToString("D"));
        if (await names.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("Original ACK cannot release a source whose owned name is still present.");
    }

    internal async Task<(MirrorPulseNamespaceMemberProtectionSnapshot Source, MirrorPulseNamespaceMemberProtectionSnapshot Target)>
        ReadAuditedFileTransferSettlementParentsAsync(Guid operationId, MirrorPulseFileTransferRecovery recovery, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            var retirement = await ReadNamespaceRetirementCoreAsync(copy.Plan.DeleteOperationId, transaction, token, copy).ConfigureAwait(false)
                ?? throw new InvalidDataException("Original ACK lost its source retirement.");
            var parents = await ReadAuditedFileTransferSettlementParentsAsync(copy, retirement, recovery, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return parents;
        }
        finally { _gate.Release(); }
    }

    private async Task<(MirrorPulseNamespaceMemberProtectionSnapshot Source, MirrorPulseNamespaceMemberProtectionSnapshot Target)>
        ReadAuditedFileTransferSettlementParentsAsync(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceRetirement retirement,
            MirrorPulseFileTransferRecovery recovery, SqliteTransaction transaction, CancellationToken token)
    {
        if (retirement is not { Phase: MirrorPulseNamespaceRetirementPhase.Completed, TransferReceipt: not null } ||
            retirement.Intent.TransferEntry?.OperationId != copy.Plan.OperationId || copy.TargetPublication is not { } publication)
            throw new InvalidOperationException("Settlement recovery requires the original published target and completed source retirement.");
        var plan = copy.Plan;
        var fences = await ReadNamespaceRootFencesCoreAsync(true, transaction, token, admittedTransferId: plan.OperationId,
            admittedTransferBirthId: copy.TargetBirthOperationId).ConfigureAwait(false);
        if (fences.Contains(plan.SourceRoute.RootId) || fences.Contains(plan.TargetRoute.RootId))
            throw new InvalidOperationException("Another original namespace owner prevents transfer settlement.");
        var (sourceAudit, targetAudit) = await RequireFileTransferAuditsAsync(plan, recovery, publication.PublishedAt,
            transaction, token).ConfigureAwait(false);
        var target = await ReadNamespaceMemberProtectionSnapshotCoreAsync(copy.TargetPublication.MemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Settlement recovery lost the original protected target.");
        await RequireCurrentNamespaceMoveLineageAsync(target.Context.Member, targetAudit.RoleSid, targetAudit.CompletedAt, transaction, token)
            .ConfigureAwait(false);
        return (await RequireAuditedFileTransferMemberAsync(plan.SourceParent, sourceAudit, transaction, token).ConfigureAwait(false),
            await RequireAuditedFileTransferMemberAsync(plan.TargetParent, targetAudit, transaction, token).ConfigureAwait(false));
    }
}
