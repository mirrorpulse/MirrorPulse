using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Retained recovery inputs at the member's current committed position.</summary>
/// <remarks>
/// This is deliberately separate from an authorization snapshot: parent and child roles may
/// differ during an interrupted rotation, and an original permission intent may still be pending.
/// First provenance and current positions remain validated. No native observation is refreshed.
/// </remarks>
public sealed record MirrorPulseNamespaceMemberRecoveryContext(MirrorPulseNamespaceMemberRegistration Member,
    MirrorPulseNamespaceMemberLocation Location, MirrorPulseNamespaceMemberIdentity Identity,
    MirrorPulseNamespacePermissionBaseline? Original, MirrorPulseNamespacePermissionChange? Permission,
    MirrorPulseNamespaceBirthProtection? BirthProtection, MirrorPulseNamespaceBirthObservation? BirthObservation);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads recovery inputs for a member and its actual parents under a pending initialization fence.</summary>
    /// <remarks>
    /// The pending owner must cover the child's root. These inputs allow the Host to plan fresh
    /// checks and replay retained work; they do not bypass ordinary current-role authorization.
    /// A completed initialization, retired member or missing reservation cannot supply them.
    /// </remarks>
    public async Task<IReadOnlyList<MirrorPulseNamespaceMemberRecoveryContext>> ReadNamespaceMemberRecoveryLineageAsync(
        Guid initializationOperationId, Guid memberId, CancellationToken cancellationToken = default)
    {
        if (initializationOperationId == Guid.Empty || memberId == Guid.Empty)
            throw new ArgumentException("A pending initialization and member ID are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceMemberRecoveryLineageCoreAsync(initializationOperationId, memberId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<MirrorPulseNamespaceMemberRecoveryContext>> ReadNamespaceMemberRecoveryLineageCoreAsync(
        Guid initializationOperationId, Guid memberId, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var admission = await ReadNamespaceInitializationStatusCoreAsync(initializationOperationId, transaction, cancellationToken).ConfigureAwait(false);
        if (admission.Completion is not null) throw new InvalidOperationException("Historical initialization cannot authorize current recovery inputs.");
        var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The recovery member is not registered.");
        if (member.RootId is not { } root || !admission.Intent.Roots.Any(candidate => candidate.RootId == root))
            throw new InvalidOperationException("The pending initialization does not own this member's root.");
        var result = new List<MirrorPulseNamespaceMemberRecoveryContext>(); var visited = new HashSet<Guid>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(member.MemberId)) throw new InvalidDataException("The current recovery lineage contains a cycle.");
            if (await ReadCompletedNamespaceRetirementForMemberCoreAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false))
                throw new FileNotFoundException("Recovery cannot adopt a retired member.");
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMemberNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            MirrorPulseNamespaceMemberRecoveryContext context;
            if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
            {
                var original = await ReadPermissionBaselineCoreAsync(member.OriginId, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The recovery original capture is missing.");
                var permission = await ReadLatestPermissionChangeCoreAsync(member.OriginId, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The recovery original permission intent is missing.");
                if (permission.Intent.EvidenceId != original.EvidenceId || permission.Intent.LocalObject != member.LocalObject ||
                    permission.Intent.RootId != member.RootId || original.LocalObject != member.LocalObject ||
                    original.OwnerSid != member.OwnerSid || original.IsDirectory != member.IsDirectory)
                    throw new InvalidDataException("Recovery cannot substitute another original object or permission intent.");
                await ValidatePermissionMemberLocationAsync(original, permission.Intent, current: false, transaction, cancellationToken).ConfigureAwait(false);
                await ValidatePermissionTreeAdmissionAsync(original, permission.Intent, transaction, cancellationToken).ConfigureAwait(false);
                // A completed move may precede the next located permission epoch. Retain
                // both facts explicitly; never patch the old intent to the current path.
                if (permission.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
                    throw new InvalidOperationException("Explicit permission restoration requires separate recovery.");
                if (permission.Phase != MirrorPulseNamespacePermissionPhase.Verified &&
                    (permission.Intent.RelativePath != location.RelativePath ||
                    permission.Intent.Relocation?.CompletedMoveOperationId != location.CurrentPositionOperationId))
                    throw new InvalidDataException("An unfinished permission intent does not match its committed current position.");
                if (permission.Phase == MirrorPulseNamespacePermissionPhase.Verified)
                    _ = await ValidateNamespaceContextPositionAsync(member, location,
                        new(permission.Intent.OperationId, permission.Intent.RoleSid, permission.Verification!.ObservedAt), permission.Verification,
                        permission.Intent.Relocation?.CompletedMoveOperationId, transaction, cancellationToken).ConfigureAwait(false);
                context = new(member, location, identity, original, permission, null, null);
            }
            else
            {
                var protection = await ReadLatestNamespaceBirthProtectionCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The recovery birth protection is missing.");
                var observation = await ReadNamespaceBirthObservationCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The recovery first birth observation is missing.");
                // Validate the epoch's historical lineage. Its actual parent may already
                // have rotated; current-role coherence must be restored before admission.
                _ = await ReadNamespaceMemberProofAsync(member, false, transaction, cancellationToken,
                    protection.ProtectionId, protection.Verification.ObservedAt).ConfigureAwait(false);
                _ = await ValidateNamespaceContextPositionAsync(member, location,
                    new(protection.ProtectionId, protection.RoleSid, protection.Verification.ObservedAt), protection.Verification,
                    protection.Placement?.PositionOperationId, transaction, cancellationToken).ConfigureAwait(false);
                context = new(member, location, identity, null, null, protection, observation);
            }
            if (result.Count != 0) RequireNamespaceRecoveryParent(result[^1], context);
            result.Add(context);
            if (location.ParentMemberId is not { } parent) break;
            member = await ReadNamespaceMemberCoreAsync(parent, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The current recovery parent lost its original provenance.");
        }
        var anchor = result[^1];
        if (anchor.Location.RelativePath.Length != 0 || anchor.Member.RootId is not null || !anchor.Member.IsDirectory ||
            anchor.Member.LocalObject.LocalFileId != anchor.Member.LocalObject.SyncRootFileId)
            throw new InvalidDataException("The recovery lineage must end at the retained sync-root anchor.");
        return result.AsReadOnly();
    }

    private static void RequireNamespaceRecoveryParent(MirrorPulseNamespaceMemberRecoveryContext child,
        MirrorPulseNamespaceMemberRecoveryContext parent)
    {
        int separator = child.Location.RelativePath.LastIndexOf('/');
        string parentPath = separator < 0 ? string.Empty : child.Location.RelativePath[..separator];
        if (child.Location.ParentMemberId != parent.Member.MemberId || !parent.Member.IsDirectory ||
            parentPath != parent.Location.RelativePath || parent.Member.OwnerSid != child.Member.OwnerSid ||
            parent.Member.RootId is { } parentRoot && parentRoot != child.Member.RootId ||
            parent.Member.LocalObject.VolumeSerialNumber != child.Member.LocalObject.VolumeSerialNumber ||
            parent.Member.LocalObject.SyncRootFileId != child.Member.LocalObject.SyncRootFileId)
            throw new InvalidDataException("Recovery requires the actual committed direct parent and same original object scope.");
    }
}
