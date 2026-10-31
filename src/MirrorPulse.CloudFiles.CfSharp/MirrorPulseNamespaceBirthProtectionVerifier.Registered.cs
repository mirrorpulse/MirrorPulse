using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public static partial class MirrorPulseNamespaceBirthProtectionVerifier
{
    /// <summary>Records verified protection at an already registered born object's current placement.</summary>
    /// <remarks>
    /// The Host retains the shared namespace actor and root/ancestor lifetime guards. This reads
    /// fresh metadata and public identities without applying permissions, converting or hydrating
    /// an object, accepting content or refreshing an immutable replay's observation time.
    /// </remarks>
    public static async Task<MirrorPulseNamespaceBirthProtection> RecordRegisteredAsync(MirrorPulseProductCatalog catalog,
        Guid protectionId, Guid memberId, CloudItem item, CloudDirectory parent, MirrorPulseRootRouter router,
        MirrorPulseNamespaceActor namespaceActor, CancellationToken cancellationToken = default)
    {
        var observed = await InspectRegisteredAsync(catalog, protectionId, memberId, item, parent, router, namespaceActor,
            null, cancellationToken).ConfigureAwait(false);
        return await catalog.RecordNamespaceBirthProtectionAsync(observed, cancellationToken).ConfigureAwait(false);
    }

    // Recovery projects this observation only after its drained scope and in the same
    // product transaction as completion. Ordinary verification retains unfenced admission.
    internal static Task<MirrorPulseNamespaceBirthProtection> InspectRegisteredAsync(MirrorPulseProductCatalog catalog,
        Guid protectionId, Guid memberId, CloudItem item, CloudDirectory parent, MirrorPulseRootRouter router,
        MirrorPulseNamespaceActor namespaceActor, MirrorPulseNamespaceMemberProtectionSnapshot? recoveryParent,
        CancellationToken cancellationToken) => InspectRegisteredCoreAsync(catalog, protectionId, memberId, item, parent, router,
            namespaceActor, recoveryParent, requireParentRole: true, cancellationToken);

    // This narrow path settles the exact original Label/initializer action under the
    // current user's native owner rights. It does not rotate the parent's older role.
    internal static async Task<MirrorPulseNamespaceBirthProtection> InspectRootLabelRecoveryAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceBornMemberRecoveryAction action, CloudItem item, CloudDirectory parent, MirrorPulseRootRouter router,
        MirrorPulseNamespaceActor namespaceActor, CancellationToken cancellationToken)
    {
        namespaceActor.AssertAccess();
        if (action.RootLabelRecoveryOwner is null ||
            await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId, cancellationToken).ConfigureAwait(false) != action)
            throw new InvalidOperationException("Label recovery verification requires its exact currently admitted original action.");
        var observed = await InspectRegisteredCoreAsync(catalog, action.ActionId, action.Child.Member.MemberId, item, parent, router,
            namespaceActor, action.Parent, requireParentRole: false, cancellationToken).ConfigureAwait(false);
        if (await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId, cancellationToken).ConfigureAwait(false) != action)
            throw new InvalidOperationException("The original Label recovery action changed during native verification.");
        return observed;
    }

    private static async Task<MirrorPulseNamespaceBirthProtection> InspectRegisteredCoreAsync(MirrorPulseProductCatalog catalog,
        Guid protectionId, Guid memberId, CloudItem item, CloudDirectory parent, MirrorPulseRootRouter router,
        MirrorPulseNamespaceActor namespaceActor, MirrorPulseNamespaceMemberProtectionSnapshot? recoveryParent, bool requireParentRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(namespaceActor);
        if (protectionId == Guid.Empty || memberId == Guid.Empty)
            throw new ArgumentException("A protection epoch and registered member ID are required.");
        cancellationToken.ThrowIfCancellationRequested();
        namespaceActor.AssertAccess();
        var retained = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(memberId, recoveryParent, cancellationToken).ConfigureAwait(false);
        var member = retained.Member;
        var birth = retained.Birth;
        var original = retained.Original;
        var location = retained.Location;
        var retainedParent = retained.Parent;
        var identity = retained.Identity;
        var parentIdentity = retained.ParentIdentity;
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.User?.Value != original.OwnerSid ||
            requireParentRole && !new WindowsPrincipal(caller).IsInRole(new SecurityIdentifier(retainedParent.Protection.RoleSid)))
            throw new UnauthorizedAccessException("The current parent namespace role and original owner are required.");
        if (member.LocalObject != original.LocalObject || member.OwnerSid != original.OwnerSid ||
            item.Parent is null || !string.Equals(item.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Registered verification requires the same native object and its actual direct parent.");
        await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, cancellationToken).ConfigureAwait(false);
        var before = await parentLease.InspectAsync(cancellationToken).ConfigureAwait(false);
        var parentSnapshot = await parent.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireRegisteredNativeObject(before, parentSnapshot, retainedParent.Member, retainedParent.Location);
        if (!before.IsDirectory || before.Dacl != retainedParent.Verification.Dacl ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(retainedParent.Verification.Dacl, before.Dacl))
            throw new InvalidDataException("The actual current parent lost its verified descriptor.");
        RequireRegisteredPublicIdentity(parentSnapshot, parentIdentity,
            retainedParent.Member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth);
        string inheritedDacl = MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(before.Dacl, member.IsDirectory);
        await using var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, cancellationToken).ConfigureAwait(false);
        var facts = await lease.InspectAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await item.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireRegisteredNativeObject(facts, snapshot, member, location);
        RequireRegisteredPublicIdentity(snapshot, identity, requirePlaceholder: true);
        if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(inheritedDacl, facts.Dacl))
            throw new InvalidDataException("The current born object does not inherit its actual parent's protection.");
        var parentAfter = await parentLease.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireUnchangedParent(before, parentAfter);
        var parentSnapshotAfter = await parent.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireRegisteredNativeObject(parentAfter, parentSnapshotAfter, retainedParent.Member, retainedParent.Location);
        RequireRegisteredPublicIdentity(parentSnapshotAfter, parentIdentity,
            retainedParent.Member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth);
        var after = await lease.InspectAsync(cancellationToken).ConfigureAwait(false);
        var snapshotAfter = await item.InspectAsync(cancellationToken).ConfigureAwait(false);
        RequireRegisteredNativeObject(after, snapshotAfter, member, location);
        RequireRegisteredPublicIdentity(snapshotAfter, identity, requirePlaceholder: true);
        if (facts.Dacl != after.Dacl)
            throw new InvalidDataException("The born object's descriptor changed during verification.");
        var retainedAfter = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(memberId, recoveryParent, cancellationToken).ConfigureAwait(false);
        if (location != retainedAfter.Location || identity != retainedAfter.Identity ||
            parentIdentity != retainedAfter.ParentIdentity || retainedParent != retainedAfter.Parent)
            throw new InvalidDataException("Current membership, identity or parent protection changed during native verification.");
        namespaceActor.AssertAccess();
        var placement = new MirrorPulseNamespaceBirthProtectionPlacement(memberId, location.CurrentPositionOperationId,
            retainedParent.Member.MemberId, retainedParent.Protection.ProtectionId,
            retainedParent.Location.CurrentPositionOperationId, location.RelativePath);
        var latest = retainedAfter.LatestProtection;
        if (latest.ProtectionId == protectionId)
        {
            if (latest.Version != 3 || latest.Placement != placement || latest.RoleSid != retainedParent.Protection.RoleSid ||
                latest.Verification.LocalObject != facts.LocalObject || latest.Verification.OwnerSid != facts.OwnerSid ||
                latest.Verification.Dacl != facts.Dacl)
                throw new InvalidDataException("A replay cannot replace a located protection epoch or refresh an earlier placement.");
            return latest;
        }
        return new(3, protectionId, member.OriginId,
            latest.ProtectionId, birth.ParentPermissionOperationId, retainedParent.Protection.RoleSid,
            new(facts.LocalObject, facts.OwnerSid, facts.Dacl, after.ObservedAt), true, facts.LinkCount!.Value)
        { BornParent = birth.BornParent, Placement = placement };
    }

    private static void RequireRegisteredNativeObject(MirrorPulseNamespacePermissionObject facts, CloudItemSnapshot snapshot,
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceMemberLocation location)
    {
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.LocalBinding is not { } binding ||
            facts.LocalObject != member.LocalObject || facts.OwnerSid != member.OwnerSid || facts.RootId != member.RootId ||
            facts.RelativePath != location.RelativePath || facts.IsDirectory != member.IsDirectory || facts.LinkCount != 1 ||
            (snapshot.Kind == CloudItemKind.Directory) != member.IsDirectory ||
            facts.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId))
            throw new InvalidDataException("The actual registered object lost its binding, owner, current path or type.");
    }

    internal static void RequireRegisteredPublicIdentity(CloudItemSnapshot snapshot,
        MirrorPulseNamespaceMemberIdentity identity, bool requirePlaceholder)
    {
        if (requirePlaceholder && (!snapshot.IsPlaceholder || snapshot.PlaceholderIdentity.IsEmpty || identity.ItemId is null || identity.RemoteId is null))
            // Report only closed native/catalog facts, never paths or public identity bytes.
            // These independent failures require different recovery; none authorizes adoption.
            throw new InvalidDataException($"The registered born object requires its retained current public identity: " +
                $"nativePlaceholder={snapshot.IsPlaceholder}; nativeIdentityPresent={!snapshot.PlaceholderIdentity.IsEmpty}; " +
                $"retainedItemPresent={identity.ItemId is not null}; retainedRemotePresent={identity.RemoteId is not null}; kind={snapshot.Kind}.");
        if (identity.ItemId is not { } itemId) return;
        if (!snapshot.IsPlaceholder || snapshot.PlaceholderIdentity.IsEmpty)
            throw new InvalidDataException("A known registered identity requires an actual placeholder.");
        var native = CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span);
        if (native.ItemId != itemId || native.RemoteId != identity.RemoteId ||
            snapshot.ItemId is { } projectedItem && projectedItem != itemId ||
            snapshot.RemoteId is { } projectedRemote && projectedRemote != identity.RemoteId)
            throw new InvalidDataException("The native public identity disagrees with the member's retained current identity.");
    }
}
