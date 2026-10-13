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
        if (action.OwnerSid != session.OwnerSid.Value || action.RoleSid != session.RoleSid.Value ||
            action.RoleOnlyDacl != MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(session))
            throw new UnauthorizedAccessException("Staged handoff requires the original owner and current derived namespace role policy.");
        _ = await ApplyOwnedFileRoleHandoffAsync(file, router, action.LocalObject, action.OwnerSid,
            [action.ExpectedDacl, action.RoleOnlyDacl], session, requireOrdinary: true, expectedIdentity: null, token).ConfigureAwait(false);
    }

    /// <summary>Moves only an admitted original file's metadata permissions to the freshly derived role.</summary>
    /// <remarks>
    /// Persist and validate the caller's original action under its shared actor before entering.
    /// Known descriptors come from that durable chain; they are never current-path substitutes.
    /// This boundary acquires no data access, reads no source and completes no product operation.
    /// Replacement callers retain the original public identity for either native file start.
    /// </remarks>
    internal static async Task<MirrorPulseNamespacePermissionObject> ApplyOwnedFileRoleHandoffAsync(CloudFile file,
        MirrorPulseRootRouter router, MirrorPulseLocalFileBinding original, string ownerSid, IReadOnlyList<string> knownDacls,
        MirrorPulseNamespaceExecutionSession session, bool requireOrdinary, CloudPlaceholderIdentity? expectedIdentity, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(router); ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(knownDacls);
        if (knownDacls.Count is < 1 or > 4 || knownDacls.Any(dacl => string.IsNullOrWhiteSpace(dacl) || dacl.Length > 65_536) ||
            !requireOrdinary && expectedIdentity is null)
            throw new ArgumentException("File handoff requires bounded owned descriptors and the original replacement identity.", nameof(knownDacls));
        string roleOnly = MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(session);
        string[] known = [.. knownDacls, roleOnly];
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        if (ownerSid != session.OwnerSid.Value || caller.User?.Value != ownerSid || !new WindowsPrincipal(caller).IsInRole(session.RoleSid))
            throw new UnauthorizedAccessException("File handoff requires the original owner executing as the current namespace role.");
        await using var metadata = await OpenCoreAsync(file, router, metadataOnlyTarget: true, ownerMetadataWrite: true, token).ConfigureAwait(false);
        var before = await metadata.InspectAsync(token).ConfigureAwait(false);
        var snapshot = await file.InspectAsync(token).ConfigureAwait(false);
        RequireOriginal(before, snapshot);
        if (!known.Any(dacl => MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(dacl, before.Dacl)))
            throw new InvalidDataException("File handoff requires a descriptor retained by its original action chain.");
        if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(roleOnly, before.Dacl))
        {
            token.ThrowIfCancellationRequested();
            _ = metadata.ReadOwnerSid(new(original.VolumeSerialNumber, original.SyncRootFileId, original.LocalFileId));
            var descriptor = new HandleSecurity(metadata._target, isDirectory: false);
            descriptor.SetSecurityDescriptorSddlForm(roleOnly, AccessControlSections.Access);
            descriptor.PersistAccess(metadata._target);
        }
        var after = await metadata.InspectAsync(token).ConfigureAwait(false);
        var afterSnapshot = await file.InspectAsync(token).ConfigureAwait(false); RequireOriginal(after, afterSnapshot);
        if (snapshot.IsPlaceholder != afterSnapshot.IsPlaceholder || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(roleOnly, after.Dacl))
            throw new InvalidDataException("Owner-metadata handoff did not retain its original native kind and current role-only policy.");
        return after;

        void RequireOriginal(MirrorPulseNamespacePermissionObject current, CloudItemSnapshot native)
        {
            if (!native.Exists || native.IsTombstone || native.Kind != CloudItemKind.File || requireOrdinary && native.IsPlaceholder ||
                current.IsDirectory || current.LinkCount != 1 || current.LocalObject != original || current.OwnerSid != ownerSid ||
                native.LocalBinding is not { } binding || new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != original ||
                native.IsPlaceholder && (expectedIdentity is null || !native.PlaceholderIdentity.Span.SequenceEqual(expectedIdentity.Encode())))
                throw new InvalidDataException("File handoff requires the original single-link object, owner and admitted public identity.");
        }
    }
}
