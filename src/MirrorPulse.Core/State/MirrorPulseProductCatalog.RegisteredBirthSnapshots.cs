using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Validated registered birth and current parent facts from one catalog read transaction.</summary>
/// <remarks>
/// This snapshot grants no native execution, current byte proof or journal acknowledgement.
/// Read a new snapshot after native inspection; never retain it as authority across mutations.
/// </remarks>
public sealed record MirrorPulseRegisteredNamespaceBirthSnapshot(MirrorPulseNamespaceMemberRegistration Member,
    MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthObservation Original,
    MirrorPulseNamespaceMemberLocation Location, MirrorPulseNamespaceMemberProtectionContext Parent,
    MirrorPulseNamespaceMemberIdentity Identity, MirrorPulseNamespaceMemberIdentity ParentIdentity,
    MirrorPulseNamespaceBirthProtection LatestProtection);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the complete retained inputs for registered birth verification in one fresh snapshot.</summary>
    /// <remarks>
    /// Ordinary reads require an unfenced parent lineage. Recovery requires the exact full parent
    /// snapshot already selected by its independent execution admission; this does not replace it.
    /// </remarks>
    public async Task<MirrorPulseRegisteredNamespaceBirthSnapshot> ReadRegisteredNamespaceBirthSnapshotAsync(Guid memberId,
        MirrorPulseNamespaceMemberProtectionSnapshot? recoveryParent = null, CancellationToken cancellationToken = default)
    {
        if (memberId == Guid.Empty) throw new ArgumentException("A member ID is required.", nameof(memberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = BeginNamespaceRecoveryReadSnapshot();
            var member = await ReadNamespaceMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The born namespace member is not registered.");
            if (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
                throw new ArgumentException("Registered birth verification requires born provenance.", nameof(memberId));
            var birth = await ReadNamespaceBirthCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original birth admission is missing.");
            var original = await ReadNamespaceBirthObservationCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The first native birth observation is missing.");
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            if (await ReadCompletedNamespaceRetirementForMemberCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false))
                throw new FileNotFoundException("The namespace member has been retired.");
            await RequireNamespaceMemberNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            if (location.ParentMemberId is not { } parentMemberId)
                throw new InvalidDataException("A registered born object requires a retained actual parent.");
            MirrorPulseNamespaceMemberProtectionContext retainedParent;
            if (recoveryParent is null)
                retainedParent = (await ReadNamespaceMemberProtectionLineageCoreAsync(parentMemberId, transaction, cancellationToken).ConfigureAwait(false))[0];
            else
            {
                var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(parentMemberId, transaction, cancellationToken).ConfigureAwait(false);
                if (current is null || current != recoveryParent)
                    throw new InvalidDataException("The admitted recovery parent changed during native verification.");
                retainedParent = current.Context;
            }
            var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            var parentMember = await ReadNamespaceMemberCoreAsync(retainedParent.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The namespace member is not registered.");
            var parentIdentity = await ReadNamespaceMemberIdentityCoreAsync(parentMember, transaction, cancellationToken).ConfigureAwait(false);
            var latest = await ReadLatestNamespaceBirthProtectionCoreAsync(member.OriginId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A registered born object lost its earlier protection epoch.");
            transaction.Commit();
            return new(member, birth, original, location, retainedParent, identity, parentIdentity, latest);
        }
        finally { _gate.Release(); }
    }
}
