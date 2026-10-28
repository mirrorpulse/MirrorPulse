using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>One exact original SDK observation, accepted only as a local root Label move.</summary>
public sealed record MirrorPulseRootLabelJournalObservation(Guid OperationId, long Sequence, MirrorPulseWorkerChangeKind Kind,
    Guid? ItemId, string RelativePath, string? PreviousRelativePath, bool IsDirectory, DateTimeOffset ObservedAt);

/// <summary>The Host's fresh public SDK root identity under its retained native tree and anchor guards.</summary>
public sealed record MirrorPulseRootLabelJournalNativeIdentity(Guid ItemId, MirrorPulseLocalFileBinding LocalObject,
    string PlaceholderIdentity, string DirectoryName, DateTimeOffset ObservedAt);

/// <summary>Retained current-policy facts, not a claim that a native call caused the notification.</summary>
public sealed record MirrorPulseRootLabelJournalWitness(RootId RootId, Guid AnchorMemberId, Guid RootMemberId,
    Guid LabelOperationId, Guid ProtectionId, Guid ItemId, MirrorPulseLocalFileBinding LocalObject,
    string OwnerSid, string RoleSid, string DirectoryName, string PlaceholderIdentity, long NameEpoch, string InventoryFingerprint,
    string LabelHistoryFingerprint, string DirectoryReceiptFingerprint, int MemberCount, DateTimeOffset AuditedAt, DateTimeOffset RootObservedAt);

/// <summary>Durable original-ID ownership before public null-revision ACK; only an actual return completes it.</summary>
public sealed record MirrorPulseRootLabelJournalAcknowledgement(int Version, MirrorPulseRootLabelJournalWitness Witness,
    MirrorPulseRootLabelJournalObservation Observation, DateTimeOffset StartedAt, DateTimeOffset? AcknowledgedAt = null);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Accepts one exact root notification under local-only Label policy after the Host's whole-root native audit.</summary>
    /// <remarks>The Host retains its actor, current role and native guards. Paths neither identify an owner nor prove
    /// causation. The catalog revalidates the complete selected-root inventory, identity, lifecycle and name epoch.
    /// This method changes no official journal, source revision, native object or live Host readiness.</remarks>
    public async Task<MirrorPulseRootLabelJournalAcknowledgement> StartRootLabelJournalAcknowledgementAsync(
        MirrorPulseNamespaceCurrentTreeSnapshot auditedInventory, string roleSid, DateTimeOffset auditedAt, MirrorPulseRootLabelJournalNativeIdentity nativeRoot,
        MirrorPulseRootLabelJournalObservation observation, DateTimeOffset startedAt, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(auditedInventory);
        ArgumentNullException.ThrowIfNull(nativeRoot);
        ArgumentNullException.ThrowIfNull(observation);
        ValidateRootLabelJournalObservation(observation);
        if (auditedInventory.ScopeRoot is not { } rootId || rootId.Value == Guid.Empty || string.IsNullOrWhiteSpace(roleSid) ||
            auditedAt == default || nativeRoot.ItemId == Guid.Empty || nativeRoot.LocalObject is null ||
            string.IsNullOrEmpty(nativeRoot.PlaceholderIdentity) || nativeRoot.PlaceholderIdentity.Length > 65536 ||
            nativeRoot.ObservedAt < auditedAt || startedAt < nativeRoot.ObservedAt || startedAt < observation.ObservedAt)
            throw new ArgumentException("A selected root, live audit role and ordered original ACK start are required.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var retained = await ReadRootLabelJournalCoreAsync(observation.OperationId, transaction, token).ConfigureAwait(false);
            if (retained is not null)
            {
                if (retained.Witness.RootId != rootId || retained.Observation != observation)
                    throw new InvalidOperationException("The original root notification already has immutable local-policy ownership.");
                transaction.Commit(); return retained;
            }
            var current = await ReadNamespaceCurrentTreeSnapshotCoreAsync(auditedInventory.AnchorMemberId, transaction, token, rootId).ConfigureAwait(false);
            if (current.Fingerprint != auditedInventory.Fingerprint || current.FencedRoots.Count != 0)
                throw new InvalidOperationException("Root notification acceptance requires the same complete unfenced audited inventory.");
            var root = current.Members.SingleOrDefault(member => member.Context.Location.ParentMemberId == current.AnchorMemberId &&
                member.Context.Member.RootId == rootId)
                ?? throw new InvalidOperationException("The selected root lost its current registered anchor child.");
            var anchor = current.Members.Single(member => member.Context.Member.MemberId == current.AnchorMemberId);
            if (!root.Context.Member.IsDirectory || nativeRoot.ItemId != observation.ItemId ||
                nativeRoot.LocalObject != root.Context.Member.LocalObject || nativeRoot.DirectoryName != root.Context.Location.RelativePath ||
                root.Identity.ItemId is { } registeredItem && registeredItem != nativeRoot.ItemId ||
                current.Members.Any(member => member.Context.Protection.RoleSid != roleSid ||
                    member.Context.Member.OwnerSid != anchor.Context.Member.OwnerSid) ||
                root.Context.Location.CurrentPositionOperationId is not { } labelId)
                throw new InvalidOperationException("An original root item and the complete current-role protected inventory are required.");
            var history = await ReadManagedRootRenameHistoryCoreAsync(labelId, token, transaction).ConfigureAwait(false);
            var move = await ReadNamespaceMemberMoveCoreAsync(labelId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The current root placement lost its original Label move.");
            var completion = await ReadNamespaceDirectoryMoveCompletionCoreAsync(labelId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The current root placement lost its complete atomic subtree receipt.");
            if (move.Version != 3 || move.RootId != rootId || move.MemberId != root.Context.Member.MemberId ||
                move.ItemId != observation.ItemId || move.LocalObject != root.Context.Member.LocalObject ||
                history.Intent.Phase != MirrorPulseRootRenamePhase.Completed || history.Proof is not { } proof || proof.ItemId != observation.ItemId ||
                proof.PlaceholderIdentity != nativeRoot.PlaceholderIdentity ||
                history.Intent.TargetName != root.Context.Location.RelativePath || history.Intent.UpdatedAt != completion.Root.VerifiedAt ||
                auditedAt < history.Intent.UpdatedAt)
                throw new InvalidOperationException("Root journal policy requires its completed original protected Label and whole-subtree placement.");
            await RequireRootLabelJournalTopologyAsync(rootId, root.Context.Location.RelativePath, transaction, token).ConfigureAwait(false);
            long epoch = await ReadNamespaceNameEpochCoreAsync(current.AnchorMemberId, root.Context.Location.RelativePath, transaction, token).ConfigureAwait(false);
            var witness = new MirrorPulseRootLabelJournalWitness(rootId, current.AnchorMemberId, root.Context.Member.MemberId,
                labelId, root.Context.Protection.ProtectionId, observation.ItemId!.Value, root.Context.Member.LocalObject,
                root.Context.Member.OwnerSid, roleSid, root.Context.Location.RelativePath, nativeRoot.PlaceholderIdentity, epoch, current.Fingerprint,
                RootLabelJournalDigest(history), await ReadRootLabelDirectoryDigestAsync(labelId, transaction, token).ConfigureAwait(false),
                current.Members.Count, auditedAt, nativeRoot.ObservedAt);
            var proposed = new MirrorPulseRootLabelJournalAcknowledgement(1, witness, observation, startedAt);
            ValidateRootLabelJournalRecord(proposed);
            await RequireNamespaceTransferIdAvailableAsync(observation.OperationId, transaction, token).ConfigureAwait(false);
            await WriteRootLabelJournalCoreAsync(proposed, true, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return proposed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records the actual successful original-ID ACK return, without refreshing any remote revision.</summary>
    public async Task<MirrorPulseRootLabelJournalAcknowledgement> CompleteRootLabelJournalAcknowledgementAsync(
        MirrorPulseRootLabelJournalAcknowledgement originalStart, DateTimeOffset acknowledgedAt, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(originalStart);
        ValidateRootLabelJournalRecord(originalStart);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var retained = await ReadRootLabelJournalCoreAsync(originalStart.Observation.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("An actual root ACK return requires its original durable first start.");
            if (originalStart.AcknowledgedAt is not null || retained with { AcknowledgedAt = null } != originalStart)
                throw new InvalidOperationException("Only the same immutable original root ACK invocation may complete.");
            if (retained.AcknowledgedAt is not null) { transaction.Commit(); return retained; }
            if (acknowledgedAt < retained.StartedAt) throw new ArgumentException("An ACK return cannot predate its first start.", nameof(acknowledgedAt));
            var completed = retained with { AcknowledgedAt = acknowledgedAt };
            await WriteRootLabelJournalCoreAsync(completed, false, transaction, token).ConfigureAwait(false);
            await using var clear = _connection.CreateCommand(); clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM blocked_local_operations WHERE operation_id=$id AND reason IN ($reconcile,$identity);";
            clear.Parameters.AddWithValue("$id", retained.Observation.OperationId.ToString("D"));
            clear.Parameters.AddWithValue("$reconcile", (int)MirrorPulseLocalOperationBlockReason.RootReconciliationRequired);
            clear.Parameters.AddWithValue("$identity", (int)MirrorPulseLocalOperationBlockReason.RequestIdentityMismatch);
            await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRootLabelJournalAcknowledgement?> ReadRootLabelJournalAcknowledgementAsync(
        Guid operationId, CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original journal ID is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadRootLabelJournalCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads this root's pending original ACK metadata without granting dispatch or native authority.</summary>
    /// <remarks>Global inventory validation and current-role admission remain separate operations.</remarks>
    public async Task<MirrorPulseRootLabelJournalAcknowledgement?> ReadPendingRootLabelJournalAcknowledgementAsync(
        RootId rootId, CancellationToken token = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("A stable root ID is required.", nameof(rootId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadPendingRootLabelJournalForRootCoreAsync(rootId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads bounded candidate IDs without interpreting another original's ownership payload.</summary>
    /// <remarks>Each candidate still requires its full immutable point-read validation before any ACK.
    /// This permits recovery to advance past one damaged original without granting native authority.</remarks>
    public async Task<IReadOnlyList<Guid>> ReadPendingRootLabelJournalOperationIdsAsync(
        Guid? afterOperationId = null, int limit = 8, CancellationToken token = default)
    {
        if (afterOperationId == Guid.Empty || limit is < 1 or > 64) throw new ArgumentException("A canonical cursor and bounded recovery size are required.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadRootLabelJournalPendingIdsCoreAsync(null, afterOperationId, limit, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads a bounded original-ID recovery page; callers retain and rotate the canonical GUID cursor.</summary>
    public async Task<IReadOnlyList<MirrorPulseRootLabelJournalAcknowledgement>> ReadPendingRootLabelJournalAcknowledgementsAsync(
        Guid? afterOperationId = null, int limit = 8, CancellationToken token = default)
    {
        if (afterOperationId == Guid.Empty || limit is < 1 or > 64) throw new ArgumentException("A canonical cursor and bounded recovery size are required.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var ids = await ReadRootLabelJournalPendingIdsCoreAsync(null, afterOperationId, limit, transaction, token).ConfigureAwait(false);
            var result = new List<MirrorPulseRootLabelJournalAcknowledgement>(ids.Count);
            foreach (Guid id in ids)
                result.Add(await ReadRootLabelJournalCoreAsync(id, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A pending root ACK lost its original owner."));
            transaction.Commit(); return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }
}
