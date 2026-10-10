using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Records a separate native protection check of an originally observed born object.</summary>
/// <remarks>
/// The Host must retain its shared namespace actor and root/ancestor lifetime guards. Metadata
/// handles alone do not freeze names. This boundary neither changes ACLs nor converts files,
/// hydrates content, accepts uploads, releases reservations or declares a tree ready.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public static class MirrorPulseNamespaceBirthProtectionVerifier
{
    public static async Task<MirrorPulseNamespaceBirthProtection> RecordAsync(MirrorPulseProductCatalog catalog,
        Guid protectionId, Guid birthOperationId, CloudItem item, CloudDirectory parent,
        MirrorPulseRootRouter router, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(router);
        if (protectionId == Guid.Empty) throw new ArgumentException("A protection epoch ID is required.", nameof(protectionId));
        var birth = await catalog.ReadNamespaceBirthAsync(birthOperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original birth admission is missing.");
        var original = await catalog.ReadNamespaceBirthObservationAsync(birthOperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original native birth observation is missing.");
        var parentOriginal = await catalog.ReadNamespacePermissionBaselineAsync(birth.ParentEvidenceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original parent evidence is missing.");
        var parentProtection = await catalog.ReadLatestNamespacePermissionChangeAsync(birth.ParentEvidenceId, cancellationToken).ConfigureAwait(false);
        if (parentProtection is not { Phase: MirrorPulseNamespacePermissionPhase.Verified, Verification: { } parentVerification } ||
            parentProtection.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
            throw new InvalidOperationException("The direct parent requires current verified protection.");
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.User?.Value != original.OwnerSid ||
            !new WindowsPrincipal(caller).IsInRole(new SecurityIdentifier(parentProtection.Intent.RoleSid)))
            throw new UnauthorizedAccessException("The current namespace role and original owner are required for protection verification.");
        await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, cancellationToken).ConfigureAwait(false);
        var before = await parentLease.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!before.IsDirectory || before.LocalObject != birth.ParentLocalObject || before.OwnerSid != parentOriginal.OwnerSid ||
            before.RootId != parentProtection.Intent.RootId || before.RelativePath != parentProtection.Intent.RelativePath ||
            before.Dacl != parentVerification.Dacl || before.LinkCount != 1 ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parentProtection.Intent.TargetDacl, before.Dacl))
            throw new InvalidDataException("The actual direct parent does not retain its verified binding, owner and protection.");
        string expectedDacl = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(before.Dacl, birth.IsDirectory);
        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, cancellationToken).ConfigureAwait(false);
        var facts = await lease.InspectAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await item.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Exists || !snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != original.LocalObject || facts.OwnerSid != original.OwnerSid ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
            facts.RootId != birth.RootId || facts.RelativePath != birth.RelativePath || facts.IsDirectory != birth.IsDirectory ||
            (snapshot.Kind == CloudItemKind.Directory) != birth.IsDirectory || facts.LinkCount != 1 ||
            snapshot.ItemId != original.ItemId || snapshot.RemoteId != original.RemoteId ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(expectedDacl, facts.Dacl))
            throw new InvalidDataException("The actual born object does not retain its original identity and inherited protection.");
        var after = await parentLease.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (after.LocalObject != before.LocalObject || after.OwnerSid != before.OwnerSid || after.Dacl != before.Dacl ||
            after.RootId != before.RootId || after.RelativePath != before.RelativePath || !after.IsDirectory || after.LinkCount != 1)
            throw new InvalidDataException("The protected parent changed during the native child verification.");
        var latest = await catalog.ReadLatestNamespaceBirthProtectionAsync(birthOperationId, cancellationToken).ConfigureAwait(false);
        if (latest?.ProtectionId == protectionId)
        {
            if (latest.ParentPermissionOperationId != parentProtection.Intent.OperationId || latest.RoleSid != parentProtection.Intent.RoleSid ||
                latest.Verification.LocalObject != facts.LocalObject || latest.Verification.OwnerSid != facts.OwnerSid ||
                latest.Verification.Dacl != facts.Dacl)
                throw new InvalidDataException("A changed parent or object cannot replace a retained protection epoch.");
            return latest;
        }
        return await catalog.RecordNamespaceBirthProtectionAsync(new(1, protectionId, birthOperationId,
            latest?.ProtectionId, parentProtection.Intent.OperationId, parentProtection.Intent.RoleSid,
            new(facts.LocalObject, facts.OwnerSid, facts.Dacl, facts.ObservedAt), true, facts.LinkCount.Value), cancellationToken).ConfigureAwait(false);
    }
}
