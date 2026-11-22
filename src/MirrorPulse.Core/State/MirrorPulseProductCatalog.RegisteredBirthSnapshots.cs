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
            var result = await ReadRegisteredNamespaceBirthSnapshotCoreAsync(memberId, recoveryParent, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Independently admits the original Label action and reads its complete registered facts in one fresh read transaction.</summary>
    /// <remarks>Invoke separately before and after native verification. This never retains admission across native operations or writes.</remarks>
    public async Task<MirrorPulseRegisteredNamespaceBirthSnapshot> ReadRootLabelBornRecoverySnapshotAsync(
        MirrorPulseNamespaceBornMemberRecoveryAction original, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.RootLabelRecoveryOwner is null)
            throw new InvalidOperationException("Label recovery verification requires its exact currently admitted original action.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = BeginNamespaceRecoveryReadSnapshot();
            if (await ValidateBornMemberRecoveryExecutionCoreAsync(original.ActionId, transaction, cancellationToken).ConfigureAwait(false) != original)
                throw new InvalidOperationException("Label recovery verification requires its exact currently admitted original action.");
            var result = await ReadRegisteredNamespaceBirthSnapshotCoreAsync(original.Child.Member.MemberId, original.Parent,
                transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseRegisteredNamespaceBirthSnapshot> ReadRegisteredNamespaceBirthSnapshotCoreAsync(Guid memberId,
        MirrorPulseNamespaceMemberProtectionSnapshot? recoveryParent, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
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
        return new(member, birth, original, location, retainedParent, identity, parentIdentity, latest);
    }
}
