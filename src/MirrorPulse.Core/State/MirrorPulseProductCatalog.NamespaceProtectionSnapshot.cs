using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Coherent retained member, position, permission and identity facts from one read transaction.</summary>
/// <remarks>Fences may be present. This read grants no native execution, current byte proof or journal ACK.</remarks>
public sealed record MirrorPulseNamespaceMemberProtectionSnapshot(MirrorPulseNamespaceMemberProtectionContext Context,
    MirrorPulseNamespaceMemberIdentity Identity, string ExpectedDacl, Guid PermissionOperationId,
    MirrorPulseNamespaceBornParent? BornParent, MirrorPulseNamespaceBirthObservation? Observation);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceMemberProtectionSnapshot?> ReadNamespaceMemberProtectionSnapshotAsync(
        Guid memberId, CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = BeginNamespaceRecoveryReadSnapshot();
            var result = await ReadNamespaceMemberProtectionSnapshotCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot?> ReadNamespaceMemberProtectionSnapshotCoreAsync(
        Guid memberId, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var member = await ReadNamespaceMemberRowAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
        if (member is null) return null;
        var lineage = await ReadNamespaceMemberProtectionLineageCoreAsync(memberId, transaction, cancellationToken,
            requireUnfenced: false).ConfigureAwait(false);
        var context = lineage[0];
        var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
        MirrorPulseNamespaceMemberProtectionSnapshot result;
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
        {
            var permission = await ReadLatestPermissionChangeCoreAsync(member.OriginId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("The registered original lost its current permission proof.");
            if (permission.Intent.OperationId != context.Protection.ProtectionId || permission.Verification != context.Verification)
                throw new InvalidDataException("The original permission snapshot is inconsistent.");
            result = new(context, identity, permission.Intent.TargetDacl, permission.Intent.OperationId, null, null);
        }
        else
        {
            var protection = await ReadLatestNamespaceBirthProtectionCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The registered birth lost its current protection proof.");
            var observation = await ReadNamespaceBirthObservationCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The registered birth lost its first native observation.");
            if (protection.ProtectionId != context.Protection.ProtectionId || protection.Verification != context.Verification)
                throw new InvalidDataException("The birth protection snapshot is inconsistent.");
            result = new(context, identity, protection.Verification.Dacl, Guid.Empty,
                new(member.OriginId, protection.ProtectionId), observation);
        }
        return result;
    }
}
