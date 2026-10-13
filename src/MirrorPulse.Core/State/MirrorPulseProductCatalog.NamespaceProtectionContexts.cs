using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Retained protection at a committed product location, not a fresh native authorization.</summary>
/// <remarks>
/// The first membership remains immutable. PositionVerification belongs to the completed move
/// of this object or its ancestor; it does not acknowledge content or refresh an ACL observation.
/// Callers must inspect each actual object and parent under the namespace execution boundary.
/// </remarks>
public sealed record MirrorPulseNamespaceMemberProtectionContext(MirrorPulseNamespaceMemberRegistration Member,
    MirrorPulseNamespaceMemberLocation Location, MirrorPulseNamespaceMemberProtectionEpoch Protection,
    MirrorPulseNamespacePermissionVerification Verification,
    MirrorPulseNamespacePermissionVerification? PositionVerification);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the member and its actual current parent lineage in one catalog transaction.</summary>
    /// <remarks>Fenced roots, retired objects and inconsistent current roles cannot supply this context.</remarks>
    public async Task<IReadOnlyList<MirrorPulseNamespaceMemberProtectionContext>> ReadNamespaceMemberProtectionLineageAsync(
        Guid memberId, CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var lineage = await ReadNamespaceMemberProtectionLineageCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return lineage;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceMemberProtectionContext>> ReadNamespaceMemberProtectionLineageCoreAsync(
        Guid memberId, SqliteTransaction transaction, CancellationToken cancellationToken, bool requireUnfenced = true)
    {
        var lineage = new List<MirrorPulseNamespaceMemberProtectionContext>();
        var visited = new HashSet<Guid>();
        var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The namespace member is not registered.");
        if (requireUnfenced && member.RootId is { } rootId)
            await RequireNamespaceMoveRootUnfencedAsync(rootId, null, transaction, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(member.MemberId)) throw new InvalidDataException("The current protection lineage contains a cycle.");
            if (await ReadCompletedNamespaceRetirementForMemberCoreAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false))
                throw new FileNotFoundException("The current protection lineage contains a retired member.");
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMemberNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            var retained = await ReadNamespaceMemberContextProtectionAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            if (member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
            {
                var epoch = await ReadNamespaceBirthProtectionRowAsync(retained.Proof.ProtectionId, transaction, cancellationToken).ConfigureAwait(false);
                if (epoch?.Protection.Placement is not null)
                    await ValidateNamespaceLocatedBirthCurrentAsync(epoch.Value.Protection, transaction, cancellationToken).ConfigureAwait(false);
            }
            var position = await ValidateNamespaceContextPositionAsync(member, location, retained.Proof, retained.Verification,
                retained.EpochPositionId, transaction, cancellationToken).ConfigureAwait(false);
            var context = new MirrorPulseNamespaceMemberProtectionContext(member, location,
                new(member.MemberId, retained.Proof.ProtectionId, retained.Proof.RoleSid, retained.Proof.VerifiedAt),
                retained.Verification, position);
            if (lineage.Count != 0) RequireNamespaceCurrentProtectionParent(lineage[^1], context);
            lineage.Add(context);
            if (location.ParentMemberId is not { } parentId) break;
            member = await ReadNamespaceMemberCoreAsync(parentId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The actual protected parent is not registered.");
        }
        return lineage.AsReadOnly();
    }

    // Historical birth lineage proves origin. Current parent roles are checked separately
    // against committed membership, so moving a born child never changes its first parent.
    private async Task<(NamespaceMemberProof Proof, MirrorPulseNamespacePermissionVerification Verification, Guid? EpochPositionId)>
        ReadNamespaceMemberContextProtectionAsync(MirrorPulseNamespaceMemberRegistration member,
            SqliteTransaction transaction, CancellationToken token)
    {
        Guid protectionId;
        MirrorPulseNamespacePermissionVerification verification;
        Guid? epochPositionId = null;
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
        {
            var permission = await ReadLatestPermissionChangeCoreAsync(member.OriginId, token, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The current original protection is missing.");
            if (permission.Phase != MirrorPulseNamespacePermissionPhase.Verified ||
                permission.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
                throw new InvalidOperationException("The current original object is not protected.");
            protectionId = permission.Intent.OperationId;
            verification = permission.Verification!;
            epochPositionId = permission.Intent.Relocation?.CompletedMoveOperationId;
            var original = await ReadPermissionBaselineCoreAsync(member.OriginId, token, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original permission capture is missing.");
            await ValidatePermissionTreeAdmissionAsync(original, permission.Intent, transaction, token).ConfigureAwait(false);
        }
        else
        {
            var protection = await ReadLatestNamespaceBirthProtectionCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The current birth protection is missing.");
            protectionId = protection.ProtectionId;
            verification = protection.Verification;
            epochPositionId = protection.Placement?.PositionOperationId;
        }
        var proof = await ReadNamespaceMemberProofAsync(member, current: false, transaction, token,
            protectionId, verification.ObservedAt).ConfigureAwait(false);
        return (proof, verification, epochPositionId);
    }

    private async Task<MirrorPulseNamespacePermissionVerification?> ValidateNamespaceContextPositionAsync(
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceMemberLocation location,
        NamespaceMemberProof proof, MirrorPulseNamespacePermissionVerification verification, Guid? epochPositionId,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (location.CurrentPositionOperationId is not { } operationId)
        {
            if (epochPositionId is not null) throw new InvalidOperationException("Protection lost its completed placement.");
            return null;
        }
        var placement = await ReadNamespaceMemberPositionEvidenceCoreAsync(member, operationId, DateTimeOffset.MaxValue,
            transaction, token).ConfigureAwait(false);
        MirrorPulseNamespacePermissionVerification observed;
        Guid retainedProtectionId;
        var move = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The protection context lost its affecting move.");
        if (move.DirectoryMoveEvidence is not null)
        {
            var completion = await ReadNamespaceDirectoryMoveCompletionCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The protection context lost its complete subtree receipt.");
            var history = _namespaceHistoryReads.GetOrCreateValue(transaction).DirectoryCompletions[operationId];
            if (!history.MemberIndexes.TryGetValue(member.MemberId, out int index))
                throw new InvalidDataException("The subtree receipt does not contain the protected member.");
            retainedProtectionId = history.Snapshot.Entries[index].ProtectionId;
            observed = completion.Entries[index].Completion.Child;
        }
        else
        {
            var completion = await ReadNamespaceMemberMoveCompletionCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The protection context lost its completed relocation.");
            retainedProtectionId = move.ProtectionId;
            observed = completion.Child;
        }
        if (proof.VerifiedAt <= placement.VerifiedAt)
        {
            if (proof.ProtectionId != retainedProtectionId || verification.LocalObject != observed.LocalObject ||
                verification.OwnerSid != observed.OwnerSid || verification.Dacl != observed.Dacl)
                throw new InvalidOperationException("Completed placement must retain this object's exact protection epoch and descriptor.");
        }
        else if (epochPositionId != operationId)
            throw new InvalidOperationException("A later protection epoch requires explicit verification at the current completed position.");
        return observed;
    }

    private static void RequireNamespaceCurrentProtectionParent(MirrorPulseNamespaceMemberProtectionContext child,
        MirrorPulseNamespaceMemberProtectionContext parent)
    {
        int separator = child.Location.RelativePath.LastIndexOf('/');
        string parentPath = separator < 0 ? string.Empty : child.Location.RelativePath[..separator];
        if (child.Location.ParentMemberId != parent.Member.MemberId || !parent.Member.IsDirectory ||
            parentPath != parent.Location.RelativePath ||
            (parent.Member.RootId is { } parentRoot && parentRoot != child.Member.RootId) ||
            parent.Member.LocalObject.VolumeSerialNumber != child.Member.LocalObject.VolumeSerialNumber ||
            parent.Member.LocalObject.SyncRootFileId != child.Member.LocalObject.SyncRootFileId ||
            parent.Member.OwnerSid != child.Member.OwnerSid || parent.Protection.RoleSid != child.Protection.RoleSid)
            throw new InvalidOperationException("Current protection requires the actual direct parent, retained object scope and matching role.");
    }
}
