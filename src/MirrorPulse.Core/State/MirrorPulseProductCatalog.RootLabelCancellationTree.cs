using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>A coherent metadata inventory scoped to one original prepared Label cancellation.</summary>
/// <remarks>This is not current move, permission, byte, readiness or journal authority.</remarks>
public sealed record MirrorPulseRootLabelCancellationTree(Guid OperationId, Guid RootMemberId,
    MirrorPulseNamespaceMemberProtectionSnapshot Anchor, IReadOnlyList<MirrorPulseNamespaceMemberProtectionSnapshot> Members)
{
    public MirrorPulseRootLabelCancellationRecoveryOwner? RecoveryOwner { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the complete original-source tree while permitting only its physical anchor to have a later role.</summary>
    public async Task<MirrorPulseRootLabelCancellationTree> ReadRootLabelCancellationTreeAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var tree = await ReadRootLabelCancellationTreeCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return tree;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseRootLabelCancellationTree> ReadRootLabelCancellationTreeCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var history = await ReadManagedRootRenameHistoryCoreAsync(operationId, token, transaction).ConfigureAwait(false);
        var current = (await ReadManagedRootRenamesCoreAsync(token, transaction).ConfigureAwait(false))
            .SingleOrDefault(item => item.RootId == history.Intent.RootId);
        if (history.Proof is not { DirectoryMoveEvidence: not null } original || history.Intent.Phase != MirrorPulseRootRenamePhase.Prepared ||
            history.Observation is not null || current != history.Intent)
            throw new InvalidOperationException("Cancellation metadata requires the exact original still-prepared public Label.");
        var move = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        if (move is null) await RequireNoUnsealedCancellationMoveOwnershipAsync(operationId, transaction, token).ConfigureAwait(false);
        else
        {
            if (move.Version != 3 || move.RemoteEntry is not null || move.RootId != history.Intent.RootId ||
                move.LocalObject != original.LocalObject || move.DirectoryMoveEvidence != original.DirectoryMoveEvidence ||
                move.Source.RelativePath != history.Intent.SourceName || move.Target.RelativePath != history.Intent.TargetName ||
                await ReadPendingNamespaceMemberMoveCoreAsync(move.RootId, transaction, token).ConfigureAwait(false) != move)
                throw new InvalidOperationException("Cancellation metadata cannot replace its original sealed ownership or reservation.");
            await RequireUnstartedRootLabelCancellationNoProgressAsync(operationId, transaction, token).ConfigureAwait(false);
        }
        var recoveryOwner = move is null
            ? await ReadRootLabelCancellationRecoveryOwnerCoreAsync(operationId, transaction, token).ConfigureAwait(false) : null;
        await RequireNamespaceMoveRootUnfencedAsync(history.Intent.RootId, move?.OperationId, transaction, token,
            allowedRootRename: operationId).ConfigureAwait(false);
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT member_id FROM namespace_members WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file;";
        AddNamespaceBirthBindingParameters(query, original.LocalObject);
        object? id = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        var root = id is null ? null : await ReadNamespaceMemberCoreAsync(ParseNamespaceMemberId(id), transaction, token).ConfigureAwait(false);
        if (root is null || !root.IsDirectory || root.RootId != history.Intent.RootId || root.LocalObject != original.LocalObject)
            throw new InvalidDataException("Cancellation metadata lost its exact original registered root binding.");
        var location = await ReadNamespaceMemberLocationCoreAsync(root, transaction, token).ConfigureAwait(false);
        if (location.RelativePath != history.Intent.SourceName || location.ParentMemberId is not { } anchorId)
            throw new InvalidOperationException("Cancellation metadata requires the unchanged original first-level source.");
        var anchorMember = await ReadNamespaceMemberCoreAsync(anchorId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cancellation metadata lost its physical anchor.");
        if (anchorMember.RootId is not null || anchorMember.ParentMemberId is not null || !anchorMember.IsDirectory ||
            anchorMember.RelativePath.Length != 0 || anchorMember.OwnerSid != root.OwnerSid ||
            anchorMember.LocalObject.VolumeSerialNumber != root.LocalObject.VolumeSerialNumber ||
            anchorMember.LocalObject.LocalFileId != root.LocalObject.SyncRootFileId || anchorMember.LocalObject.SyncRootFileId != root.LocalObject.SyncRootFileId)
            throw new InvalidOperationException("Cancellation metadata cannot substitute another physical anchor or current-user owner.");
        query.Parameters.Clear(); query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        string? payload = (string?)await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        var registration = payload is null ? null : DeserializeTopology(payload).Roots.SingleOrDefault(item => item.RootId == root.RootId);
        if (registration is null || registration.DirectoryName != history.Intent.SourceName ||
            registration.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
            throw new InvalidOperationException("Cancellation metadata cannot reinterpret a changed root mapping or lifecycle.");
        var anchor = await ReadRootCancellationMemberSnapshotCoreAsync(anchorMember, transaction, token).ConfigureAwait(false);
        if (anchor.Context.Location.ParentMemberId is not null || anchor.Context.Location.RelativePath.Length != 0)
            throw new InvalidOperationException("Cancellation metadata requires the unchanged physical anchor position.");
        if (move is not null)
        {
            await RequireNamespaceMoveParentPositionAsync(anchorMember, move.Source, move.PreparedAt, true, transaction, token).ConfigureAwait(false);
            await RequireNamespaceMoveTargetAvailableAsync(move, transaction, token).ConfigureAwait(false);
        }
        var tree = await ReadNamespaceMemberTreeCoreAsync(root.MemberId, transaction, token).ConfigureAwait(false);
        if (tree.Count is < 1 or > 65_536) throw new InvalidOperationException("Cancellation metadata requires a bounded complete original-source tree.");
        var contexts = new Dictionary<Guid, MirrorPulseNamespaceMemberProtectionContext> { [anchorId] = anchor.Context };
        var members = new List<MirrorPulseNamespaceMemberProtectionSnapshot>(tree.Count);
        foreach (var entry in tree.OrderBy(item => item.Depth))
        {
            var member = await ReadNamespaceMemberCoreAsync(entry.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The current cancellation tree lost a registered member.");
            var snapshot = await ReadRootCancellationMemberSnapshotCoreAsync(member, transaction, token, recoveryOwner, root.MemberId).ConfigureAwait(false);
            var context = snapshot.Context;
            if (member.RootId != root.RootId || member.OwnerSid != root.OwnerSid || member.LocalObject != entry.LocalObject ||
                member.IsDirectory != entry.IsDirectory || context.Location.ParentMemberId != entry.ParentMemberId ||
                context.Location.RelativePath != entry.RelativePath || entry.Depth != entry.RelativePath.Count(character => character == '/') ||
                context.Location.ParentMemberId is not { } parentId || !contexts.TryGetValue(parentId, out var parent))
                throw new InvalidDataException("Cancellation metadata requires every exact current direct parent, binding, kind and position.");
            if (member.MemberId == root.MemberId)
            {
                if (parentId != anchorId || entry.Depth != 0 || context.Location.RelativePath != history.Intent.SourceName)
                    throw new InvalidOperationException("Only the original first-level root may cross the retained anchor role boundary.");
            }
            else if (recoveryOwner is null) RequireNamespaceCurrentProtectionParent(context, parent);
            else RequireRootCancellationRecoveryParent(context, parent);
            if (!contexts.TryAdd(member.MemberId, context)) throw new InvalidDataException("The cancellation inventory repeats a member.");
            members.Add(snapshot);
        }
        if (move is not null)
        {
            var sealedTree = await ReadNamespaceDirectoryMoveSnapshotCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Cancellation metadata lost its original sealed tree.");
            if (sealedTree.Entries.Count != members.Count) throw new InvalidOperationException("Cancellation metadata cannot change sealed membership.");
            var byId = members.ToDictionary(item => item.Context.Member.MemberId);
            foreach (var entry in sealedTree.Entries)
            {
                if (!byId.TryGetValue(entry.MemberId, out var snapshot) || snapshot.Context.Member.LocalObject != entry.LocalObject ||
                    snapshot.Context.Member.IsDirectory != entry.IsDirectory || snapshot.Context.Location.ParentMemberId != entry.ParentMemberId ||
                    snapshot.Context.Location.RelativePath != entry.RelativePath || snapshot.Context.Location.CompletedMoveOperationId != entry.PreviousMoveOperationId ||
                    snapshot.Context.Location.PositionOperationId != entry.PreviousPositionOperationId || snapshot.Identity != entry.Identity ||
                    snapshot.Context.Protection.ProtectionId != entry.ProtectionId || snapshot.Context.Protection.VerifiedAt != entry.ProtectionVerifiedAt ||
                    snapshot.Context.Protection.RoleSid != move.RoleSid)
                    throw new InvalidOperationException("Cancellation metadata must retain all sealed descendant identity, position and protection epochs.");
            }
        }
        return new(operationId, root.MemberId, anchor, members.AsReadOnly()) { RecoveryOwner = recoveryOwner };
    }

    // Metadata only, scoped by the exact pending initializer above. Every
    // object's own verified descriptor is inspected natively before cancellation.
    // This does not authorize ordinary current-role work or release any fence.
    private static void RequireRootCancellationRecoveryParent(MirrorPulseNamespaceMemberProtectionContext child,
        MirrorPulseNamespaceMemberProtectionContext parent)
    {
        int separator = child.Location.RelativePath.LastIndexOf('/');
        string parentPath = separator < 0 ? string.Empty : child.Location.RelativePath[..separator];
        if (child.Location.ParentMemberId != parent.Member.MemberId || !parent.Member.IsDirectory ||
            parentPath != parent.Location.RelativePath ||
            parent.Member.RootId is { } parentRoot && parentRoot != child.Member.RootId ||
            parent.Member.LocalObject.VolumeSerialNumber != child.Member.LocalObject.VolumeSerialNumber ||
            parent.Member.LocalObject.SyncRootFileId != child.Member.LocalObject.SyncRootFileId ||
            parent.Member.OwnerSid != child.Member.OwnerSid)
            throw new InvalidOperationException("Cancellation recovery requires the actual committed direct parent and retained original object scope.");
    }

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot> ReadRootCancellationMemberSnapshotCoreAsync(
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token,
        MirrorPulseRootLabelCancellationRecoveryOwner? recoveryOwner = null, Guid? originalRootMemberId = null)
    {
        if (await ReadCompletedNamespaceRetirementForMemberCoreAsync(member.MemberId, transaction, token).ConfigureAwait(false))
            throw new FileNotFoundException("Cancellation metadata cannot adopt a retired member.");
        var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMemberNameAsync(member, transaction, token).ConfigureAwait(false);
        var retained = await ReadNamespaceMemberContextProtectionAsync(member, transaction, token).ConfigureAwait(false);
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
        {
            var epoch = await ReadNamespaceBirthProtectionRowAsync(retained.Proof.ProtectionId, transaction, token).ConfigureAwait(false);
            // The original first-level root can retain a located epoch under an
            // older physical-anchor role. Historical lineage and committed position
            // remain mandatory; native cancellation inspects its exact old descriptor.
            // Descendants still require their actual current parent unless owned by
            // the separately validated original initializer.
            if (epoch?.Protection.Placement is not null && recoveryOwner is null && member.MemberId != originalRootMemberId)
                await ValidateNamespaceLocatedBirthCurrentAsync(epoch.Value.Protection, transaction, token).ConfigureAwait(false);
        }
        var position = await ValidateNamespaceContextPositionAsync(member, location, retained.Proof, retained.Verification,
            retained.EpochPositionId, transaction, token).ConfigureAwait(false);
        var context = new MirrorPulseNamespaceMemberProtectionContext(member, location,
            new(member.MemberId, retained.Proof.ProtectionId, retained.Proof.RoleSid, retained.Proof.VerifiedAt), retained.Verification, position);
        var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false);
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
        {
            var permission = await ReadLatestPermissionChangeCoreAsync(member.OriginId, token, transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("Cancellation metadata lost the current original permission proof.");
            if (permission.Intent.OperationId != context.Protection.ProtectionId || permission.Verification != context.Verification)
                throw new InvalidDataException("Cancellation permission metadata is inconsistent.");
            return new(context, identity, permission.Intent.TargetDacl, permission.Intent.OperationId, null, null);
        }
        var protection = await ReadLatestNamespaceBirthProtectionCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cancellation metadata lost the current birth protection.");
        var observation = await ReadNamespaceBirthObservationCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Cancellation metadata lost the first native birth observation.");
        if (protection.ProtectionId != context.Protection.ProtectionId || protection.Verification != context.Verification)
            throw new InvalidDataException("Cancellation birth metadata is inconsistent.");
        return new(context, identity, protection.Verification.Dacl, Guid.Empty, new(member.OriginId, protection.ProtectionId), observation);
    }
}
