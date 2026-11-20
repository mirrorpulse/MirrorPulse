using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The actual committed placement and parent protection of a later born-object epoch.</summary>
/// <remarks>First birth parent references remain separate and immutable. Null position IDs mean the first registered placement.</remarks>
public sealed record MirrorPulseNamespaceBirthProtectionPlacement(Guid MemberId, Guid? PositionOperationId,
    Guid ParentMemberId, Guid ParentProtectionId, Guid? ParentPositionOperationId, string RelativePath);

public sealed partial class MirrorPulseProductCatalog
{
    private static void ValidateNamespaceBirthProtectionPlacement(MirrorPulseNamespaceBirthProtectionPlacement placement)
    {
        if (placement.MemberId == Guid.Empty || placement.ParentMemberId == Guid.Empty || placement.ParentProtectionId == Guid.Empty ||
            placement.MemberId == placement.ParentMemberId || placement.PositionOperationId == Guid.Empty || placement.ParentPositionOperationId == Guid.Empty)
            throw new ArgumentException("Located birth protection requires a registered object and separate protected parent.");
        ValidatePermissionRelativePath(placement.RelativePath);
        if (string.IsNullOrWhiteSpace(placement.RelativePath)) throw new ArgumentException("A located birth requires a child path.");
    }

    // Follow located parent and predecessor epochs iteratively. Reject future facts before
    // opening their move references; a completed move cannot depend on its own later epoch.
    private async Task ValidateNamespaceLocatedBirthHistoryAsync(IReadOnlyList<MirrorPulseNamespaceBirthProtection> seeds,
        SqliteTransaction? transaction, CancellationToken token)
    {
        if (transaction is null)
        {
            using SqliteTransaction owned = _connection.BeginTransaction(deferred: true);
            await ValidateNamespaceLocatedBirthHistoryAsync(seeds, owned, token).ConfigureAwait(false);
            owned.Commit(); return;
        }
        var pending = new Queue<MirrorPulseNamespaceBirthProtection>(seeds);
        var visited = new HashSet<Guid>();
        var history = _namespaceHistoryReads.GetOrCreateValue(transaction);
        var validated = new List<MirrorPulseNamespaceBirthProtection>();
        while (pending.TryDequeue(out var epoch))
        {
            token.ThrowIfCancellationRequested();
            if (history.LocatedBirthGraphs.TryGetValue(epoch.ProtectionId, out var verified) && verified == epoch) continue;
            if (!visited.Add(epoch.ProtectionId)) continue;
            var placement = epoch.Placement ?? throw new InvalidDataException("The located protection reference is missing.");
            var birth = await ReadNamespaceBirthRowAsync(epoch.BirthOperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The first located object's birth is missing.");
            var collected = new List<MirrorPulseNamespaceBirthProtection>();
            _ = await ValidateNamespaceBirthLineageAsync(birth, epoch, false, null, transaction, token, collected).ConfigureAwait(false);
            var member = await ReadNamespaceMemberRowAsync(placement.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("Located birth protection requires an already registered member.");
            await ValidateNamespaceLocatedBirthMemberAsync(member, birth, epoch, collected, transaction, token).ConfigureAwait(false);
            var ownPosition = await ReadNamespaceLocatedBirthPositionAsync(member, placement.PositionOperationId,
                epoch.Verification.ObservedAt, transaction, token).ConfigureAwait(false);
            if (ownPosition.RelativePath != placement.RelativePath || ownPosition.ParentMemberId != placement.ParentMemberId)
                throw new InvalidOperationException("Located birth protection must retain the same completed object placement and direct parent.");
            var parent = await ReadNamespaceMemberRowAsync(placement.ParentMemberId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The located protection's actual parent is missing.");
            var parentPosition = await ReadNamespaceLocatedBirthPositionAsync(parent, placement.ParentPositionOperationId,
                epoch.Verification.ObservedAt, transaction, token).ConfigureAwait(false);
            int separator = placement.RelativePath.LastIndexOf('/');
            string parentPath = separator < 0 ? string.Empty : placement.RelativePath[..separator];
            if (!parent.IsDirectory || parentPosition.RelativePath != parentPath ||
                (parent.RootId is { } parentRoot && parentRoot != member.RootId) || parent.OwnerSid != member.OwnerSid ||
                parent.LocalObject.VolumeSerialNumber != member.LocalObject.VolumeSerialNumber ||
                parent.LocalObject.SyncRootFileId != member.LocalObject.SyncRootFileId)
                throw new InvalidOperationException("Located protection requires the exact protected parent in the same retained object scope.");
            if (parent.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
            {
                var proof = await ReadNamespaceMemberProofAsync(parent, false, transaction, token, placement.ParentProtectionId,
                    epoch.Verification.ObservedAt.AddTicks(-1)).ConfigureAwait(false);
                if (proof.RoleSid != epoch.RoleSid) throw new InvalidOperationException("Located protection requires its parent's verified role.");
            }
            else
            {
                var parentEpoch = await ReadNamespaceBirthProtectionRowAsync(placement.ParentProtectionId, transaction, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The actual born parent's protection is missing.");
                if (parentEpoch.Protection.BirthOperationId != parent.OriginId || parentEpoch.Protection.RoleSid != epoch.RoleSid ||
                    parentEpoch.Protection.Verification.ObservedAt >= epoch.Verification.ObservedAt)
                    throw new InvalidOperationException("The actual parent protection must precede this epoch and retain its role.");
                var parentBirth = await ReadNamespaceBirthRowAsync(parent.OriginId, transaction, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The actual parent's first birth is missing.");
                await ValidateNamespaceLocatedBirthMemberAsync(parent, parentBirth, parentEpoch.Protection, collected, transaction, token).ConfigureAwait(false);
                _ = await ValidateNamespaceBirthLineageAsync(parentBirth, parentEpoch.Protection, false, null, transaction, token, collected).ConfigureAwait(false);
                if (parentEpoch.Protection.Placement is not null) pending.Enqueue(parentEpoch.Protection);
            }
            var previous = await ReadNamespaceBirthProtectionRowAsync(epoch.PreviousProtectionId!.Value, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The located protection predecessor is missing.");
            if (previous.Protection.BirthOperationId != epoch.BirthOperationId || previous.Protection.Verification.ObservedAt >= epoch.Verification.ObservedAt)
                throw new InvalidOperationException("Located protection must follow the same born object's earlier epoch.");
            var current = await ReadNamespaceBirthProtectionRowAsync(epoch.ProtectionId, transaction, token).ConfigureAwait(false);
            if (current is not null && previous.Sequence >= current.Value.Sequence)
                throw new InvalidDataException("The located protection predecessor has invalid sequence order.");
            _ = await ValidateNamespaceBirthLineageAsync(birth, previous.Protection, false, null, transaction, token, collected).ConfigureAwait(false);
            if (previous.Protection.Placement is not null) pending.Enqueue(previous.Protection);
            foreach (var reference in collected) pending.Enqueue(reference);
            validated.Add(epoch);
        }
        // Publish only after the entire queued historical graph passed, never a
        // partially visited seed. Match the complete immutable value, not just its ID.
        // Current placement, latest protection, names, fences and native observations
        // keep their separate checks; a later transaction starts with no entries.
        foreach (var epoch in validated) history.LocatedBirthGraphs[epoch.ProtectionId] = epoch;
    }

    private async Task ValidateNamespaceLocatedBirthMemberAsync(MirrorPulseNamespaceMemberRegistration member,
        MirrorPulseNamespaceBirthIntent birth, MirrorPulseNamespaceBirthProtection epoch,
        List<MirrorPulseNamespaceBirthProtection> locatedParents, SqliteTransaction transaction, CancellationToken token)
    {
        if (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || member.OriginId != birth.OperationId ||
            epoch.BirthOperationId != birth.OperationId || member.RootId != birth.RootId || member.RelativePath != birth.RelativePath ||
            member.LocalObject != epoch.Verification.LocalObject || member.OwnerSid != epoch.Verification.OwnerSid ||
            member.IsDirectory != birth.IsDirectory || member.RegisteredAt >= epoch.Verification.ObservedAt && epoch.Placement is not null)
            throw new InvalidOperationException("Located protection must retain an earlier registered birth, object, owner and type.");
        var first = await ReadNamespaceBirthProtectionRowAsync(member.ProtectionId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original registered birth protection is missing.");
        if (first.Protection.BirthOperationId != birth.OperationId || first.Protection.Placement is not null ||
            first.Protection.Verification.ObservedAt > member.RegisteredAt)
            throw new InvalidOperationException("Located protection cannot replace first membership with a later epoch.");
        _ = await ValidateNamespaceBirthLineageAsync(birth, first.Protection, false, null, transaction, token, locatedParents).ConfigureAwait(false);
        var firstParent = member.ParentMemberId is { } id
            ? await ReadNamespaceMemberRowAsync(id, transaction, token).ConfigureAwait(false) : null;
        if (firstParent is null || firstParent.LocalObject != birth.ParentLocalObject || firstParent.OriginId != NamespaceBirthParentId(birth) ||
            firstParent.Origin != (birth.BornParent is null ? MirrorPulseNamespaceMemberOrigin.CapturedOriginal : MirrorPulseNamespaceMemberOrigin.ProtectedBirth))
            throw new InvalidOperationException("Located protection cannot change the member's first registered parent.");
    }

    private async Task<NamespaceMemberPositionEvidence> ReadNamespaceLocatedBirthPositionAsync(
        MirrorPulseNamespaceMemberRegistration member, Guid? operationId, DateTimeOffset observedAt,
        SqliteTransaction transaction, CancellationToken token)
    {
        // This raw time check intentionally precedes full history validation, including
        // legacy completions. It prevents a later epoch being used by its own position.
        if (operationId is { } id)
        {
            var row = await ReadNamespaceMemberPositionRowAsync(member.MemberId, id, transaction, token).ConfigureAwait(false);
            var legacy = row is null ? await ReadNamespaceMemberMoveCompletionRowAsync(id, transaction, token).ConfigureAwait(false) : null;
            if ((row?.VerifiedAt ?? legacy?.Completion.VerifiedAt) is { } settled && settled >= observedAt)
                throw new InvalidOperationException("Located protection must follow its completed position.");
        }
        else if (member.RegisteredAt >= observedAt)
            throw new InvalidOperationException("Located protection must follow first membership.");
        return await ReadNamespaceMemberPositionEvidenceCoreAsync(member, operationId, observedAt, transaction, token).ConfigureAwait(false);
    }

    private async Task ValidateNamespaceLocatedBirthCurrentAsync(MirrorPulseNamespaceBirthProtection epoch,
        SqliteTransaction transaction, CancellationToken token, bool admission = false)
    {
        var placement = epoch.Placement!;
        var member = await ReadNamespaceMemberRowAsync(placement.MemberId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The located member is missing.");
        var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMemberNameAsync(member, transaction, token).ConfigureAwait(false);
        bool retainedPlacement = location.CurrentPositionOperationId == placement.PositionOperationId;
        if (admission && !retainedPlacement)
            throw new InvalidOperationException("A new located epoch requires the latest completed member position.");
        if (retainedPlacement && (location.RelativePath != placement.RelativePath || location.ParentMemberId != placement.ParentMemberId))
            throw new InvalidOperationException("Located protection lost its retained current placement.");
        if (!retainedPlacement)
            _ = await ValidateNamespaceContextPositionAsync(member, location,
                new(epoch.ProtectionId, epoch.RoleSid, epoch.Verification.ObservedAt), epoch.Verification,
                placement.PositionOperationId, transaction, token).ConfigureAwait(false);
        var parent = location.ParentMemberId is { } parentId
            ? await ReadNamespaceMemberRowAsync(parentId, transaction, token).ConfigureAwait(false) : null;
        if (parent is null) throw new FileNotFoundException("The current located parent is missing.");
        var parentLocation = await ReadNamespaceMemberLocationCoreAsync(parent, transaction, token).ConfigureAwait(false);
        await RequireNamespaceMemberNameAsync(parent, transaction, token).ConfigureAwait(false);
        var parentProof = await ReadNamespaceMemberContextProtectionAsync(parent, transaction, token).ConfigureAwait(false);
        if (retainedPlacement && (parentLocation.CurrentPositionOperationId != placement.ParentPositionOperationId ||
                parentProof.Proof.ProtectionId != placement.ParentProtectionId) || parentProof.Proof.RoleSid != epoch.RoleSid ||
            await ReadCompletedNamespaceRetirementForMemberCoreAsync(member.MemberId, transaction, token).ConfigureAwait(false) ||
            await ReadCompletedNamespaceRetirementForMemberCoreAsync(parent.MemberId, transaction, token).ConfigureAwait(false))
            throw new InvalidOperationException("Located protection requires its current committed placement and latest actual parent protection.");
    }
}
