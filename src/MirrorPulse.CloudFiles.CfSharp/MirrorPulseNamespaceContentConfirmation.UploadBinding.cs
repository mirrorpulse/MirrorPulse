using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceContentConfirmation
{
    /// <summary>Captures registered lineage before any source upload, under the shared actor.</summary>
    /// <remarks>The caller holds its normal-user content reader and instance scheduler. No source work runs in this scope.</remarks>
    public async ValueTask<MirrorPulseUploadBinding> CaptureUploadBindingAsync(CloudFile file, CloudItemSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var binding = MirrorPulseContentConfirmation.CaptureUploadBinding(snapshot);
        if (namespaceActor is null)
        {
            if (await catalog.ReadNamespaceMemberByBindingAsync(binding.LocalObject, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Registered upload capture requires the Host's shared namespace actor and execution role.");
            return binding;
        }
        return await namespaceActor.RunAsync(async token =>
        {
            namespaceActor.AssertAccess();
            var member = await catalog.ReadNamespaceMemberByBindingAsync(binding.LocalObject, token).ConfigureAwait(false);
            if (member is null) return binding;
            if (namespaceExecution is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                throw new InvalidOperationException("Protected upload capture requires the current Windows namespace execution role.");
            return await CaptureProtectedUploadBindingAsync(file, binding, member, namespaceExecution, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task<MirrorPulseUploadBinding> CaptureProtectedUploadBindingAsync(CloudFile file, MirrorPulseUploadBinding binding,
        MirrorPulseNamespaceMemberRegistration member, MirrorPulseNamespaceExecutionSession execution, CancellationToken token)
    {
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
        var route = router.ResolvePath(file.RelativePath);
        if (member.IsDirectory || member.RootId is null || location.ParentMemberId is null ||
            location.RelativePath != file.RelativePath.Replace('\\', '/') || router.GetRegistration(route.InstanceId, route.RootKey).RootId != member.RootId ||
            (await catalog.ReadNamespaceRecoveryRootsAsync(token).ConfigureAwait(false)).Contains(member.RootId.Value))
            throw new InvalidOperationException("Upload capture requires the registered file's committed location and unfenced root.");
        var parentMember = await catalog.ReadNamespaceMemberAsync(location.ParentMemberId.Value, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The upload parent is not registered.");
        var parentLocation = await catalog.ReadNamespaceMemberLocationAsync(parentMember.MemberId, token).ConfigureAwait(false);
        var childProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
        var parentProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        if (childProof.RoleSid != execution.RoleSid.Value || parentProof.RoleSid != execution.RoleSid.Value ||
            childProof.OwnerSid != execution.OwnerSid.Value || parentProof.OwnerSid != execution.OwnerSid.Value)
            throw new InvalidOperationException("Upload capture requires the current owner and child/parent protection roles.");
        CloudDirectory parent = file.Parent ?? throw new InvalidDataException("A registered upload requires a direct parent.");
        MirrorPulseUploadBinding captured = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
            await using var childLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, token).ConfigureAwait(false);
            RequireProtectedObject(await parentLease.InspectAsync(token).ConfigureAwait(false), await parent.InspectAsync(token).ConfigureAwait(false),
                parentMember, parentProof, parentLocation.RelativePath);
            var fresh = await file.InspectAsync(token).ConfigureAwait(false);
            RequireProtectedObject(await childLease.InspectAsync(token).ConfigureAwait(false), fresh, member, childProof, location.RelativePath);
            if (MirrorPulseContentConfirmation.CaptureUploadBinding(fresh) != binding)
                throw new InvalidDataException("The file binding or native identity changed before upload capture.");
            var identity = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId, token).ConfigureAwait(false);
            if (identity.ItemId is not { } itemId || identity.RemoteId is not { } remoteId)
                throw new InvalidDataException("Registered upload capture requires its retained public identity.");
            if (binding.Preparation == MirrorPulseContentPreparation.ReplacePlaceholderIdentity)
            {
                var previous = ReadPreviousIdentity(binding, member.MemberId);
                if (previous.ItemId != itemId || previous.RemoteId != remoteId)
                    throw new InvalidDataException("The native upload identity does not follow this member's retained lineage.");
                captured = binding;
            }
            else if (binding.Preparation == MirrorPulseContentPreparation.ConvertRegularFile)
                captured = binding with { NamespaceIdentity = new(member.MemberId, itemId, remoteId, identity.AcceptanceOperationId) };
            else throw new InvalidDataException("Registered upload capture requires a public conversion or identity replacement.");
        }).ConfigureAwait(false);
        return captured;
    }
}
