using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Only the original accepted conditional transfer delete may own this source retirement.</summary>
public sealed record MirrorPulseNamespaceTransferRetirementReference(int Version, Guid OperationId,
    Guid DeleteOperationId, DateTimeOffset SourceDeleteAcceptedAt, Guid TargetProtectionId,
    MirrorPulseTransferTargetVerification TargetAtStart)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceMemberProtectionSnapshot? RecoveredSourceParent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceMemberProtectionSnapshot? RecoveredTargetParent { get; init; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal MirrorPulseFileTransferRecovery? PublishedRecovery { get; init; }
}

/// <summary>Actual public SDK tombstone and unchanged parent after deletion of the guarded original source.</summary>
public sealed record MirrorPulseNamespaceTransferRetirementReceipt(int Version, Guid OperationId,
    Guid DeleteOperationId, Guid OfficialItemId, MirrorPulseLocalFileBinding LocalObject,
    bool NativeMissing, bool OfficialTombstone, MirrorPulseNamespacePermissionVerification Parent,
    DateTimeOffset VerifiedAt);

internal sealed record MirrorPulseNamespaceTransferRetirementContinuation(MirrorPulseFileTransferRecovery Recovery,
    MirrorPulseNamespaceMemberProtectionSnapshot SourceParent, Guid TargetProtectionId,
    MirrorPulseTransferTargetVerification TargetProof, DateTimeOffset SelectedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reserves the original source name for transfer-owned retirement, retaining both root fences.</summary>
    /// <remarks>The Host must retain current native source/parents/target guards, private bytes and the
    /// write-excluding source data object. This admission is not evidence that native deletion happened.</remarks>
    public Task<MirrorPulseNamespaceRetirement> PrepareNamespaceTransferSourceRetirementAsync(Guid operationId,
        MirrorPulseTransferTargetVerification freshTarget, Guid targetProtectionId, DateTimeOffset preparedAt,
        CancellationToken token = default) =>
        PrepareNamespaceTransferSourceRetirementAsync(operationId, freshTarget, targetProtectionId, preparedAt, null, token);

    internal async Task<MirrorPulseNamespaceRetirement> PrepareNamespaceTransferSourceRetirementAsync(Guid operationId,
        MirrorPulseTransferTargetVerification freshTarget, Guid targetProtectionId, DateTimeOffset preparedAt,
        MirrorPulseFileTransferRecovery? recovery, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(freshTarget);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            var selectedAudit = recovery ?? copy.TargetRecovery;
            var members = selectedAudit is null ? null : await ReadFileTransferRecoveryMembersCoreAsync(copy.Plan, selectedAudit,
                transaction, token, recovery is null ? null : copy.TargetPublication!.PublishedAt).ConfigureAwait(false);
            int version = recovery is not null ? 5 : members is null ? 3 : 4;
            var reference = new MirrorPulseNamespaceTransferRetirementReference(version, operationId, copy.Plan.DeleteOperationId,
                copy.SourceDeletion?.AcceptedAt ?? throw new InvalidOperationException("Source retirement requires its owned conditional delete acceptance."),
                targetProtectionId, freshTarget)
            {
                RecoveredSourceParent = members?.SourceParent,
                RecoveredTargetParent = recovery is null ? null : members!.TargetParent,
                PublishedRecovery = recovery
            };
            var source = members?.Source ?? copy.Plan.Source;
            var intent = new MirrorPulseNamespaceRetirementIntent(3, copy.Plan.DeleteOperationId, source.Context.Member.MemberId,
                copy.Plan.SourceRoute.RootId, source.Context.Member.LocalObject, source.Context.Location.RelativePath, false,
                source.Context.Member.OwnerSid, source.Context.Protection.RoleSid, preparedAt)
            { TransferEntry = reference };
            ValidateNamespaceRetirementIntent(intent); ValidateTransferRetirementReference(copy, intent);
            var existing = await ReadNamespaceRetirementCoreAsync(intent.OperationId, transaction, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Intent != intent) throw new InvalidOperationException("The original transfer retirement admission is immutable.");
                transaction.Commit(); return existing;
            }
            var target = await RequireCurrentTransferSourceDeletionAsync(copy, transaction, token, recovery).ConfigureAwait(false);
            if (target.Context.Protection.ProtectionId != targetProtectionId)
                throw new InvalidOperationException("Source retirement requires the current target protection epoch.");
            await RequireNamespaceMemberNameAsync(source.Context.Member, transaction, token).ConfigureAwait(false);
            await RequireNoRegisteredDescendantsAsync(intent.MemberId, transaction, token).ConfigureAwait(false);
            var retirement = new MirrorPulseNamespaceRetirement(intent.OperationId, intent, MirrorPulseNamespaceRetirementPhase.Prepared);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retirement, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("The original source retirement must be bounded.", nameof(freshTarget));
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_member_retirements(operation_id,member_id,payload,fingerprint,phase) VALUES($id,$member,$payload,$hash,0);";
            insert.Parameters.AddWithValue("$id", intent.OperationId.ToString("D")); insert.Parameters.AddWithValue("$member", intent.MemberId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return retirement;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retains the original public source item before the first guarded SDK deletion.</summary>
    public Task<MirrorPulseNamespaceRetirement> StartNamespaceTransferSourceRetirementAsync(Guid operationId,
        MirrorPulseNamespaceTransferSourceProjection projection, DateTimeOffset startedAt, CancellationToken token = default) =>
        StartNamespaceTransferSourceRetirementAsync(operationId, projection, startedAt, null, null, null, null, token);

    internal async Task<MirrorPulseNamespaceRetirement> StartNamespaceTransferSourceRetirementAsync(Guid operationId,
        MirrorPulseNamespaceTransferSourceProjection projection, DateTimeOffset startedAt, MirrorPulseFileTransferRecovery? recovery,
        Guid? targetProtectionId, MirrorPulseTransferTargetVerification? targetProof, long? observedPlaceholderLocalFileId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(projection);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var (copy, retirement) = await ReadTransferRetirementAsync(operationId, transaction, token).ConfigureAwait(false);
            ValidateTransferSourceProjection(copy, retirement, projection, startedAt);
            var recoveredLocalFileId = retirement.RecoveredTransferOfficialLocalFileId;
            if (retirement.StartedAt is not null)
            {
                var original = retirement.CurrentTransferSourceProjection;
                if (original is null)
                    throw new InvalidOperationException("The original pre-deletion SDK projection cannot be replaced or adopted after a legacy start.");
                var observed = projection with { CapturedAt = original.CapturedAt };
                if (original != observed)
                {
                    if (recovery is null || original.OfficialLocalFileId is not null || observedPlaceholderLocalFileId is not { } localId ||
                        observed.OfficialLocalFileId != localId || original != observed with { OfficialLocalFileId = null })
                        throw new InvalidOperationException("The original pre-deletion SDK projection cannot be replaced.");
                    recoveredLocalFileId = localId;
                }
                else if (recovery is null || retirement.TransferContinuation?.Recovery == recovery)
                {
                    transaction.Commit(); return retirement;
                }
            }
            if (retirement.Intent.TransferEntry!.Version is not (2 or 3 or 4 or 5))
                throw new InvalidOperationException("A legacy retirement requires explicit recovery; its original admission cannot be rewritten.");
            if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Prepared || startedAt < retirement.Intent.PreparedAt)
                throw new InvalidOperationException("The original local retirement start must follow admission.");
            var target = await RequireCurrentTransferSourceDeletionAsync(copy, transaction, token,
                recovery ?? retirement.Intent.TransferEntry.PublishedRecovery).ConfigureAwait(false);
            if (target.Context.Protection.ProtectionId != (targetProtectionId ?? retirement.Intent.TransferEntry!.TargetProtectionId))
                throw new InvalidOperationException("The target epoch changed after original source retirement admission.");
            var continuation = retirement.TransferContinuation;
            if (recovery is not null)
            {
                var members = await ReadFileTransferRecoveryMembersCoreAsync(copy.Plan, recovery, transaction, token,
                    copy.TargetPublication!.PublishedAt).ConfigureAwait(false);
                continuation = new(recovery, members.SourceParent, target.Context.Protection.ProtectionId,
                    targetProof ?? throw new ArgumentNullException(nameof(targetProof)), startedAt);
                await ValidateTransferRetirementContinuationAsync(copy, continuation, retirement.StartedAt ?? startedAt,
                    transaction, token).ConfigureAwait(false);
            }
            var started = retirement with
            {
                StartedAt = retirement.StartedAt ?? startedAt,
                TransferSourceProjection = retirement.TransferSourceProjection ?? projection,
                TransferContinuation = continuation,
                RecoveredTransferOfficialLocalFileId = recoveredLocalFileId,
                StartedItemId = copy.Plan.Source.Identity.ItemId
                ?? throw new InvalidDataException("Source retirement requires the original public logical item.")
            };
            await WriteNamespaceRetirementCoreAsync(started, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return started;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records an actual original native deletion and public SDK tombstone; no journal is acknowledged.</summary>
    public async Task<MirrorPulseNamespaceRetirement> RecordNamespaceTransferSourceRetiredAsync(Guid operationId,
        MirrorPulseNamespaceTransferRetirementReceipt receipt, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var (copy, retirement) = await ReadTransferRetirementAsync(operationId, transaction, token).ConfigureAwait(false);
            ValidateTransferRetirementReceipt(copy, retirement, receipt);
            if (retirement.TransferReceipt is not null)
            {
                if (retirement.TransferReceipt != receipt) throw new InvalidOperationException("The first original native retirement receipt is immutable.");
                transaction.Commit(); return retirement;
            }
            if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Prepared)
                throw new InvalidOperationException("A source retirement cannot replace a different terminal outcome.");
            var deleted = retirement with
            {
                Phase = MirrorPulseNamespaceRetirementPhase.NativeDeleted,
                NativeDeletedAt = receipt.VerifiedAt,
                TransferReceipt = receipt
            };
            await WriteNamespaceRetirementCoreAsync(deleted, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return deleted;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Settles only the original source name after its native receipt, keeping transfer fences and journal ownership.</summary>
    public async Task<MirrorPulseNamespaceRetirement> CompleteNamespaceTransferSourceRetirementAsync(Guid operationId,
        DateTimeOffset completedAt, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var (copy, retirement) = await ReadTransferRetirementAsync(operationId, transaction, token).ConfigureAwait(false);
            if (retirement.Phase == MirrorPulseNamespaceRetirementPhase.Completed) { transaction.Commit(); return retirement; }
            if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.NativeDeleted || retirement.TransferReceipt is null ||
                completedAt < retirement.NativeDeletedAt)
                throw new InvalidOperationException("Source name settlement requires its original native/public retirement receipt.");
            await RequireNoRegisteredDescendantsAsync(retirement.Intent.MemberId, transaction, token).ConfigureAwait(false);
            var location = await ReadNamespaceMemberLocationCoreAsync(copy.Plan.Source.Context.Member, transaction, token).ConfigureAwait(false);
            if (location != copy.Plan.Source.Context.Location)
                throw new InvalidDataException("The original source position changed before transfer name settlement.");
            await RequireNamespaceMemberNameAsync(copy.Plan.Source.Context.Member, transaction, token).ConfigureAwait(false);
            await using var remove = _connection.CreateCommand(); remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;";
            remove.Parameters.AddWithValue("$member", retirement.Intent.MemberId.ToString("D"));
            if (await remove.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The transfer source lost its original owned name before settlement.");
            long nameEpoch = await ReadNamespaceNameEpochCoreAsync(copy.Plan.SourceParent.Context.Member.MemberId,
                copy.Plan.Source.Context.Location.RelativePath, transaction, token).ConfigureAwait(false);
            var completed = retirement with { Phase = MirrorPulseNamespaceRetirementPhase.Completed, CompletedAt = completedAt, TransferSourceNameEpoch = nameEpoch };
            await WriteNamespaceRetirementCoreAsync(completed, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }

    private async Task<(MirrorPulseNamespaceTransferCopy Copy, MirrorPulseNamespaceRetirement Retirement)> ReadTransferRetirementAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original transfer is missing.");
        var retirement = await ReadNamespaceRetirementCoreAsync(copy.Plan.DeleteOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original source retirement is missing.");
        if (retirement.Intent.TransferEntry?.OperationId != operationId)
            throw new InvalidDataException("Another owner cannot retire the original transfer source.");
        return (copy, retirement);
    }

    private async Task ValidateTransferRetirementReferencesAsync(MirrorPulseNamespaceRetirement retirement,
        SqliteTransaction? transaction, CancellationToken token, MirrorPulseNamespaceTransferCopy? ownedTransfer = null)
    {
        if (retirement.Intent.TransferEntry is not { } reference)
        {
            if (retirement.TransferReceipt is not null || retirement.TransferSourceProjection is not null ||
                retirement.TransferSourceNameEpoch is not null || retirement.TransferContinuation is not null ||
                retirement.RecoveredTransferOfficialLocalFileId is not null)
                throw new InvalidDataException("A non-transfer retirement borrowed transfer generation evidence.");
            return;
        }
        var copy = ownedTransfer ?? await ReadNamespaceTransferCoreAsync(reference.OperationId,
            transaction ?? throw new InvalidDataException("Transfer retirement validation requires a consistent transaction."), token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Source retirement lost its original transfer.");
        ValidateTransferRetirementReference(copy, retirement.Intent);
        if (retirement.TransferSourceProjection is { } projection)
        {
            if (reference.Version is not (2 or 3 or 4 or 5) || retirement.StartedAt is not { } startedAt)
                throw new InvalidDataException("An unstarted retirement cannot claim an original SDK projection.");
            ValidateTransferSourceProjection(copy, retirement, projection, startedAt);
        }
        else if (reference.Version is 2 or 3 or 4 or 5 && retirement.StartedAt is not null)
            throw new InvalidDataException("The original retirement start lost its pre-deletion SDK generation.");
        if (retirement.RecoveredTransferOfficialLocalFileId is not null &&
            (retirement.TransferSourceProjection is not { OfficialLocalFileId: null } || retirement.TransferContinuation is null))
            throw new InvalidDataException("A recovered SDK file identifier requires its original projection and retirement continuation.");
        if (retirement.TransferContinuation is { } continuation)
        {
            if (retirement.StartedAt is not { } start)
                throw new InvalidDataException("The source retirement continuation lost its original SDK start.");
            await ValidateTransferRetirementContinuationAsync(copy, continuation, start,
                transaction ?? throw new InvalidDataException("The source retirement continuation requires a consistent read."), token).ConfigureAwait(false);
        }
        if (retirement.TransferSourceNameEpoch is { } epoch && (epoch < 2 || retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed || reference.Version == 1) ||
            reference.Version is 3 or 4 or 5 && retirement.Phase == MirrorPulseNamespaceRetirementPhase.Completed && retirement.TransferSourceNameEpoch is null)
            throw new InvalidDataException("The source retirement lost its original settled-name generation witness.");
        if (reference.PublishedRecovery is { } recovery)
        {
            var consistent = transaction ?? throw new InvalidDataException("Recovered retirement requires a consistent historical read.");
            var (sourceAudit, targetAudit) = await RequireFileTransferAuditsAsync(copy.Plan, recovery, copy.TargetPublication!.PublishedAt,
                consistent, token).ConfigureAwait(false);
            var sourceParent = reference.RecoveredSourceParent!;
            var targetParent = reference.RecoveredTargetParent!;
            if (sourceAudit.CompletedAt > retirement.Intent.PreparedAt || targetAudit.CompletedAt > retirement.Intent.PreparedAt ||
                sourceParent.Context.Protection.VerifiedAt > sourceAudit.CompletedAt ||
                targetParent.Context.Protection.VerifiedAt > targetAudit.CompletedAt)
                throw new InvalidDataException("Source retirement lost its original post-publication parent audits.");
            await RequireRemoteUpdateHistoricalSnapshotAsync(sourceParent, retirement.Intent.PreparedAt, consistent, token).ConfigureAwait(false);
            await RequireRemoteUpdateHistoricalSnapshotAsync(targetParent, retirement.Intent.PreparedAt, consistent, token).ConfigureAwait(false);
        }
        else if (reference.RecoveredSourceParent is { } parent)
        {
            var consistent = transaction ?? throw new InvalidDataException("Recovered retirement requires a consistent historical read.");
            await RequireRemoteUpdateHistoricalSnapshotAsync(parent, retirement.Intent.PreparedAt, consistent, token).ConfigureAwait(false);
            var audit = await ReadNamespaceInitializationStatusCoreAsync(copy.TargetRecovery!.SourceAuditId, consistent, token)
                .ConfigureAwait(false);
            if (audit.Completion is not { } completed || parent.Context.Protection.VerifiedAt > completed.CompletedAt)
                throw new InvalidDataException("Source retirement lost its original audited source parent proof.");
        }
        var protection = await ReadNamespaceBirthProtectionCoreAsync(reference.TargetProtectionId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Source retirement lost its original target protection epoch.");
        if (protection.BirthOperationId != copy.TargetBirth!.Birth.OperationId ||
            protection.Verification.LocalObject != copy.TargetPublication!.LocalObject ||
            protection.RoleSid != retirement.Intent.RoleSid || protection.Verification.ObservedAt > retirement.Intent.PreparedAt ||
            retirement.StartedItemId is { } item && item != copy.Plan.Source.Identity.ItemId ||
            (retirement.StartedAt is not null) != (retirement.StartedItemId is not null) ||
            retirement.Phase == MirrorPulseNamespaceRetirementPhase.Prepared && (retirement.NativeDeletedAt is not null || retirement.CompletedAt is not null) ||
            retirement.Phase == MirrorPulseNamespaceRetirementPhase.NativeDeleted && retirement.CompletedAt is not null ||
            retirement.Phase == MirrorPulseNamespaceRetirementPhase.Completed && retirement.CompletedAt is null ||
            retirement.Phase is not (MirrorPulseNamespaceRetirementPhase.Prepared or MirrorPulseNamespaceRetirementPhase.NativeDeleted or MirrorPulseNamespaceRetirementPhase.Completed) ||
            (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Prepared) != (retirement.TransferReceipt is not null))
            throw new InvalidDataException("The source retirement lost its exact target epoch, original item or native receipt.");
        if (retirement.TransferReceipt is { } receipt)
        {
            ValidateTransferRetirementReceipt(copy, retirement, receipt);
            if (retirement.NativeDeletedAt != receipt.VerifiedAt || retirement.CompletedAt is { } complete && complete < receipt.VerifiedAt)
                throw new InvalidDataException("The source retirement chronology disagrees with its original native receipt.");
        }
    }

    private async Task ValidateTransferRetirementContinuationAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceTransferRetirementContinuation continuation, DateTimeOffset startedAt, SqliteTransaction transaction, CancellationToken token)
    {
        var plan = copy.Plan;
        var (sourceAudit, targetAudit) = await RequireFileTransferAuditsAsync(plan, continuation.Recovery,
            copy.TargetPublication!.PublishedAt, transaction, token).ConfigureAwait(false);
        var sourceParent = continuation.SourceParent;
        var target = await ReadNamespaceBirthProtectionCoreAsync(continuation.TargetProtectionId, transaction, token).ConfigureAwait(false);
        if (continuation.SelectedAt < startedAt || sourceAudit.CompletedAt > continuation.SelectedAt || targetAudit.CompletedAt > continuation.SelectedAt ||
            sourceParent.Context.Member != plan.SourceParent.Context.Member || sourceParent.Context.Location != plan.SourceParent.Context.Location ||
            sourceParent.Identity != plan.SourceParent.Identity || sourceParent.Context.Protection.RoleSid != sourceAudit.RoleSid ||
            sourceParent.Context.Protection.VerifiedAt > sourceAudit.CompletedAt ||
            target is null || target.BirthOperationId != copy.TargetBirth!.Birth.OperationId ||
            target.Verification.LocalObject != copy.TargetPublication.LocalObject || target.RoleSid != targetAudit.RoleSid ||
            target.Verification.ObservedAt > targetAudit.CompletedAt || continuation.TargetProof.VerifiedAt < targetAudit.CompletedAt ||
            continuation.TargetProof.VerifiedAt > continuation.SelectedAt || continuation.TargetProof.RemoteId != copy.TargetVerification!.RemoteId)
            throw new InvalidDataException("The source retirement continuation lost its original audited parent, target or chronology.");
        await RequireRemoteUpdateHistoricalSnapshotAsync(sourceParent, continuation.SelectedAt, transaction, token).ConfigureAwait(false);
        ValidateNamespaceTransferTargetVerification(copy, continuation.TargetProof);
    }

    private static void ValidateTransferRetirementReference(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceRetirementIntent intent)
    {
        var reference = intent.TransferEntry!; var source = copy.Plan.Source;
        var role = reference.PublishedRecovery?.RoleSid ??
            (reference.Version == 4 ? copy.TargetRoleSid : source.Context.Protection.RoleSid);
        if (reference.Version is not (1 or 2 or 3 or 4 or 5) || reference.OperationId != copy.Plan.OperationId || reference.DeleteOperationId != copy.Plan.DeleteOperationId ||
            copy.SourceDeletion?.AcceptedAt is not { } accepted || reference.SourceDeleteAcceptedAt != accepted || copy.TargetPublication is null ||
            copy.TargetBirth is null || source.Identity.ItemId is null || reference.TargetProtectionId == Guid.Empty || reference.TargetAtStart is null ||
            reference.TargetAtStart.RemoteId != copy.TargetVerification!.RemoteId || reference.TargetAtStart.VerifiedAt < accepted ||
            intent.PreparedAt < reference.TargetAtStart.VerifiedAt || intent.OperationId != copy.Plan.DeleteOperationId ||
            intent.MemberId != source.Context.Member.MemberId || intent.RootId != copy.Plan.SourceRoute.RootId ||
            intent.LocalObject != source.Context.Member.LocalObject || intent.RelativePath != source.Context.Location.RelativePath || intent.IsDirectory ||
            intent.OwnerSid != source.Context.Member.OwnerSid || intent.RoleSid != role ||
            (reference.Version is 4 or 5) != (reference.RecoveredSourceParent is not null) ||
            (reference.Version == 5) != (reference.PublishedRecovery is not null) ||
            (reference.Version == 5) != (reference.RecoveredTargetParent is not null) ||
            reference.Version != 5 && (reference.Version == 4) != (copy.TargetRecovery is not null))
            throw new InvalidDataException("Source retirement requires the original accepted conditional transfer and a fresh original target proof.");
        RequireOriginalParent(reference.RecoveredSourceParent, copy.Plan.SourceParent);
        RequireOriginalParent(reference.RecoveredTargetParent, copy.Plan.TargetParent);
        ValidateNamespaceTransferTargetVerification(copy, reference.TargetAtStart);

        void RequireOriginalParent(MirrorPulseNamespaceMemberProtectionSnapshot? parent, MirrorPulseNamespaceMemberProtectionSnapshot original)
        {
            if (parent is not null && (parent.Context.Member != original.Context.Member || parent.Context.Location != original.Context.Location ||
                parent.Identity != original.Identity || parent.Context.Protection.RoleSid != intent.RoleSid))
                throw new InvalidDataException("Recovered source retirement requires its unchanged original parent under the audited role.");
        }
    }

    private static void ValidateTransferRetirementReceipt(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceRetirement retirement,
        MirrorPulseNamespaceTransferRetirementReceipt receipt)
    {
        var parent = retirement.TransferContinuation?.SourceParent ?? retirement.Intent.TransferEntry!.RecoveredSourceParent ?? copy.Plan.SourceParent;
        if (retirement.StartedAt is null || receipt.Version != 1 || receipt.OperationId != copy.Plan.OperationId ||
            receipt.DeleteOperationId != copy.Plan.DeleteOperationId || receipt.OfficialItemId != copy.Plan.Source.Identity.ItemId ||
            retirement.StartedItemId != receipt.OfficialItemId || receipt.LocalObject != copy.Plan.Source.Context.Member.LocalObject ||
            !receipt.NativeMissing || !receipt.OfficialTombstone || receipt.Parent is null || receipt.VerifiedAt < retirement.StartedAt ||
            receipt.Parent.LocalObject != parent.Context.Member.LocalObject || receipt.Parent.OwnerSid != parent.Context.Member.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parent.ExpectedDacl, receipt.Parent.Dacl) ||
            receipt.Parent.ObservedAt < (retirement.TransferContinuation?.SelectedAt ?? retirement.StartedAt) ||
            receipt.Parent.ObservedAt > receipt.VerifiedAt)
            throw new InvalidDataException("Source retirement requires its original guarded SDK deletion, tombstone and unchanged direct parent.");
    }

    private static void RejectGenericTransferRetirement(MirrorPulseNamespaceRetirementIntent intent)
    {
        if (intent.TransferEntry is not null)
            throw new InvalidOperationException("An original transfer source retirement requires its dedicated guarded continuation.");
    }
}
