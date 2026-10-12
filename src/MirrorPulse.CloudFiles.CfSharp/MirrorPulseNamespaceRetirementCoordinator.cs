using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Executes one protected leaf deletion and settles its durable member name.</summary>
/// <remarks>
/// The caller supplies a shared actor and the Host's execution role. Ordinary filesystem
/// deletion remains denied by the birth/parent DACL. A directory is accepted only when the
/// native filesystem itself confirms it is empty; registered descendants are rejected during
/// catalog admission. Native success and membership settlement are separate receipts.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceRetirementCoordinator(CloudFileSystem fileSystem,
    MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    public ValueTask<MirrorPulseNamespaceRetirement> PrepareAsync(Guid operationId, Guid memberId,
        CancellationToken cancellationToken = default) => actor.RunAsync(token => PrepareAdmittedAsync(operationId, memberId, token), cancellationToken);

    private async ValueTask<MirrorPulseNamespaceRetirement> PrepareAdmittedAsync(Guid operationId, Guid memberId, CancellationToken token)
    {
        actor.AssertAccess();
        var member = await catalog.ReadNamespaceMemberAsync(memberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The namespace member is not registered.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(memberId, token).ConfigureAwait(false);
        if (member.RootId is not { } rootId) throw new InvalidOperationException("Retirement requires a managed root member.");
        var proof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
        return await catalog.PrepareNamespaceRetirementAsync(new(1, operationId, memberId, rootId, member.LocalObject,
            location.RelativePath, member.IsDirectory, member.OwnerSid, proof.RoleSid, DateTimeOffset.UtcNow), token).ConfigureAwait(false);
    }

    public ValueTask<MirrorPulseNamespaceRetirement> ExecuteAsync(Guid operationId,
        CancellationToken cancellationToken = default) => actor.RunAsync(token => ExecuteAdmittedAsync(operationId, token), cancellationToken);

    private async ValueTask<MirrorPulseNamespaceRetirement> ExecuteAdmittedAsync(Guid operationId, CancellationToken token)
    {
        actor.AssertAccess();
        var current = await catalog.ValidateNamespaceRetirementExecutionAsync(operationId, token).ConfigureAwait(false);
        if (current.Phase == MirrorPulseNamespaceRetirementPhase.Completed) return current;
        var member = await catalog.ReadNamespaceMemberAsync(current.Intent.MemberId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The retired member provenance is missing.");
        if (member.RootId != current.Intent.RootId || member.LocalObject != current.Intent.LocalObject || member.OwnerSid != current.Intent.OwnerSid)
            throw new InvalidDataException("The retirement member provenance changed.");
        CloudItem item = member.IsDirectory ? fileSystem.GetDirectory(current.Intent.RelativePath) : fileSystem.GetFile(current.Intent.RelativePath);
        CloudDirectory parent = item.Parent ?? throw new InvalidDataException("A controlled retirement requires a direct parent.");
        var parentMember = member.ParentMemberId is { } parentId
            ? await catalog.ReadNamespaceMemberAsync(parentId, token).ConfigureAwait(false)
            : null;
        if (parentMember is null) throw new InvalidDataException("The retirement parent member is missing.");
        var parentProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token).ConfigureAwait(false);
        var childProof = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, member, token).ConfigureAwait(false);
        if (parentProof.RoleSid != current.Intent.RoleSid || childProof.RoleSid != current.Intent.RoleSid ||
            childProof.OwnerSid != current.Intent.OwnerSid || childProof.RootId != current.Intent.RootId)
            throw new InvalidOperationException("The retirement parent protection role is not retained.");
        await ExecuteUnderNamespaceRoleAsync(current, member, item, parent, parentProof, token).ConfigureAwait(false);
        return await catalog.ReadNamespaceRetirementAsync(operationId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("The retirement receipt disappeared after execution.");
    }

    private async Task ExecuteUnderNamespaceRoleAsync(MirrorPulseNamespaceRetirement current,
        MirrorPulseNamespaceMemberRegistration member, CloudItem item, CloudDirectory parent,
        MirrorPulseNamespaceProtectionProof parentProof, CancellationToken token)
    {
        await execution.RunNamespaceOperationAsync(async () =>
        {
            RequireCaller(current.Intent);
            await using var parentLease = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
            var parentFacts = await parentLease.InspectAsync(token).ConfigureAwait(false);
            if (parentFacts.LocalObject != parentProof.Verification.LocalObject || parentFacts.OwnerSid != parentProof.OwnerSid ||
                parentFacts.Dacl != parentProof.Verification.Dacl || parentFacts.LinkCount != 1)
                throw new InvalidDataException("The retirement parent changed before the controlled deletion.");
            var snapshot = await item.InspectAsync(token).ConfigureAwait(false);
            if (current.Phase == MirrorPulseNamespaceRetirementPhase.NativeDeleted && snapshot.Exists)
                throw new InvalidDataException("A native-deleted retirement target reappeared before membership settlement.");
            if (snapshot.Exists)
            {
                if (snapshot.LocalBinding is not { } binding ||
                    new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != current.Intent.LocalObject ||
                    snapshot.Kind != (member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File) || snapshot.IsTombstone)
                    throw new InvalidDataException("The retirement target changed before deletion.");
                await item.DeleteAsync(token).ConfigureAwait(false);
            }
            if ((await item.InspectAsync(CancellationToken.None).ConfigureAwait(false)).Exists)
                throw new IOException("The controlled retirement target still exists after deletion.");
            if (current.Phase == MirrorPulseNamespaceRetirementPhase.Prepared)
                _ = await catalog.RecordNamespaceRetirementNativeDeletedAsync(current.OperationId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            _ = await catalog.CompleteNamespaceRetirementAsync(current.OperationId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static void RequireCaller(MirrorPulseNamespaceRetirementIntent intent)
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (caller.User?.Value != intent.OwnerSid || !new WindowsPrincipal(caller).IsInRole(new SecurityIdentifier(intent.RoleSid)))
            throw new UnauthorizedAccessException("Controlled retirement requires the original owner and protected namespace role.");
    }
}
