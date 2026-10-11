using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseWindowsNamespacePermissionLease
{
    // This ordinary-file-only handoff is the one owner-metadata write outside a CfSharp
    // protected scope. It grants only the freshly derived role-only birth policy. The caller
    // must first persist and currently admit the original action under its shared actor.
    internal static async Task ApplyStagedBirthRoleHandoffAsync(CloudFile file, MirrorPulseRootRouter router,
        MirrorPulseNamespaceBirthRecoveryAction action, MirrorPulseNamespaceExecutionSession session, CancellationToken token)
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (action.OwnerSid != session.OwnerSid.Value || caller.User?.Value != action.OwnerSid ||
            action.RoleSid != session.RoleSid.Value || !new WindowsPrincipal(caller).IsInRole(session.RoleSid) ||
            action.RoleOnlyDacl != MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(session))
            throw new UnauthorizedAccessException("Staged handoff requires the original owner and current derived namespace role policy.");
        await using var metadata = await OpenCoreAsync(file, router, metadataOnlyTarget: true, ownerMetadataWrite: true, token).ConfigureAwait(false);
        var before = await metadata.InspectAsync(token).ConfigureAwait(false);
        var snapshot = await file.InspectAsync(token).ConfigureAwait(false);
        if (!snapshot.Exists || snapshot.IsPlaceholder || snapshot.IsTombstone || before.IsDirectory || before.LinkCount != 1 ||
            before.LocalObject != action.LocalObject || before.OwnerSid != action.OwnerSid ||
            !new[] { action.ExpectedDacl, action.RoleOnlyDacl }.Any(dacl => MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(dacl, before.Dacl)))
            throw new InvalidDataException("Staged handoff requires the original ordinary object and an owned descriptor state.");
        if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, before.Dacl))
        {
            token.ThrowIfCancellationRequested();
            _ = metadata.ReadOwnerSid(new(action.LocalObject.VolumeSerialNumber, action.LocalObject.SyncRootFileId, action.LocalObject.LocalFileId));
            var descriptor = new HandleSecurity(metadata._target, isDirectory: false);
            descriptor.SetSecurityDescriptorSddlForm(action.RoleOnlyDacl, AccessControlSections.Access);
            descriptor.PersistAccess(metadata._target);
        }
        var after = await metadata.InspectAsync(token).ConfigureAwait(false);
        if (after.LocalObject != action.LocalObject || after.OwnerSid != action.OwnerSid || after.LinkCount != 1 ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, after.Dacl))
            throw new InvalidDataException("The staged owner-metadata handoff did not retain its original object and role-only policy.");
    }
}
