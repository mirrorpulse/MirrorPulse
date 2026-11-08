using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Applies the first role-only permission action to the original resident placeholder.</summary>
/// <remarks>
/// The caller owns the Host lifetime, instance scheduler and pending root admission. The shared
/// actor must be held. This boundary cannot write bytes, change identity, restore access or
/// acknowledge the journal. Its receipt is not a durable native completion or a replay permit.
/// A different execution role requires a separate recovery handoff.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictKeepRemoteResidentSeal(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal async Task<CloudProtectedLocalOperationResult> ApplyFirstAsync(RootId admittedRoot,
        MirrorPulseConflictKeepRemoteCopies copies, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copies);
        actor.AssertAccess(); token.ThrowIfCancellationRequested();
        var intent = copies.Preparation.Intent;
        var start = await catalog.ReadConflictKeepRemoteFileStartAsync(intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original KeepRemote file start is missing.");
        var original = start.Original.Context;
        var parent = start.Parent.Context;
        var root = router.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey);
        _ = await MirrorPulseKeepRemotePublicIdentity.ReadOriginalAsync(catalog, root, intent, token).ConfigureAwait(false);
        if (original.Member.RootId != admittedRoot || parent.Member.RootId != admittedRoot ||
            original.Protection.RoleSid != execution.RoleSid.Value || parent.Protection.RoleSid != execution.RoleSid.Value ||
            original.Member.OwnerSid != execution.OwnerSid.Value || parent.Member.OwnerSid != execution.OwnerSid.Value ||
            copies.Local is null || intent.LocalContent?.UploadBinding.PreviousPlaceholderIdentity is not { } encodedIdentity ||
            !CloudPlaceholderIdentity.TryDecode(Convert.FromBase64String(encodedIdentity), out var identity) || identity is null ||
            identity.ItemId != intent.ItemId)
            throw new InvalidDataException("The first resident seal requires the original file, parent, selected identity and execution role.");
        CloudFile file = fileSystem.GetFile(original.Location.RelativePath);
        CloudDirectory directParent = fileSystem.GetDirectory(parent.Location.RelativePath);
        if (file.Parent is null || !string.Equals(file.Parent.FullPath, directParent.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The original file is no longer under its retained direct parent.");
        if (root.RootId != admittedRoot || root.State != RootRegistrationState.Active)
            throw new InvalidOperationException("Resident KeepRemote permission admission requires the retained active root.");

        using var store = await execution.RunNormalUserOperationAsync(() => state.OpenStoreFileLeaseAsync(token).AsTask()).ConfigureAwait(false);
        var storeBinding = new MirrorPulseCfSharpStoreFileBinding(start.StoreVolumeSerialNumber, start.StoreFileId);
        var journal = new MirrorPulseConflictKeepRemoteJournal(state.OpenStore, catalog, router, feed);
        bool retainedAction = await catalog.ReadConflictKeepRemoteFileActionAsync(intent.CommandId, token).ConfigureAwait(false) is not null;
        await RequireOriginalsAsync(token, retainedAction).ConfigureAwait(false);
        CloudProtectedLocalOperationResult result = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            actor.AssertAccess();
            if (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(original.Member.MemberId, token).ConfigureAwait(false) != start.Original ||
                await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parent.Member.MemberId, token).ConfigureAwait(false) != start.Parent)
                throw new InvalidDataException("The first resident seal cannot apply after the original catalog protection changed.");
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(directParent, router, token).ConfigureAwait(false);
            var parentBefore = await parentGuard.InspectAsync(token).ConfigureAwait(false);
            RequireObserved(parentBefore, start.Parent, start.Parent.ExpectedDacl);
            var before = await file.InspectAsync(token).ConfigureAwait(false);
            if (!before.Exists || !before.IsPlaceholder || before.IsTombstone || before.ContentAvailability != CloudContentAvailability.FullyAvailable ||
                before.LocalBinding is not { } binding || original.Member.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber,
                    binding.SyncRootFileId, binding.LocalFileId) || !before.PlaceholderIdentity.Span.SequenceEqual(identity.Encode()))
                throw new InvalidDataException("The first seal requires the exact original fully resident placeholder without source recall.");
            string roleOnly = MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution);
            var firstAction = await catalog.ReadConflictKeepRemoteFileActionAsync(intent.CommandId, token).ConfigureAwait(false);
            if (firstAction is not null && (firstAction.RoleSid != execution.RoleSid.Value || firstAction.OwnerSid != execution.OwnerSid.Value ||
                firstAction.RoleOnlyDacl != roleOnly || firstAction.RestoreDacl != start.Original.ExpectedDacl))
                throw new InvalidOperationException("The original permission action does not belong to the current role and exact resident policy.");

            bool changed = false;
            result = await MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(file, router, original.Member.LocalObject,
                async (lease, stop) =>
                {
                    var localIdentity = await lease.InspectLocalIdentityAsync(stop).ConfigureAwait(false);
                    if (!localIdentity.IsPlaceholder || localIdentity.ItemId != identity.ItemId || localIdentity.RemoteId != identity.RemoteId ||
                        !identity.Equals(localIdentity.PlaceholderIdentity))
                        throw new InvalidDataException("The retained placeholder identity changed before sealing.");
                    var observed = await lease.InspectAsync(stop).ConfigureAwait(false);
                    var action = firstAction;
                    if (action is null)
                    {
                        // Do not adopt a role-only descriptor that predates this command. Commit
                        // the first permission intent only while the original object is retained.
                        RequireObserved(observed, start.Original, start.Original.ExpectedDacl);
                        RequireParentHandle(stop);
                        action = await catalog.RetainConflictKeepRemoteFileActionAsync(new(1, intent.CommandId, Guid.NewGuid(), original.Member.LocalObject,
                            execution.OwnerSid.Value, execution.RoleSid.Value, roleOnly, start.Original.ExpectedDacl, DateTimeOffset.UtcNow), stop).ConfigureAwait(false);
                    }
                    if (!MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, observed.Dacl))
                    {
                        RequireObserved(observed, start.Original, action.RestoreDacl);
                        RequireParentHandle(stop);
                        await lease.ApplyDaclAsync(action.RoleOnlyDacl, stop).ConfigureAwait(false);
                        changed = true;
                    }
                    RequireObserved(await lease.InspectAsync(stop).ConfigureAwait(false), start.Original, action.RoleOnlyDacl);
                    RequireParentHandle(stop);
                }, token).ConfigureAwait(false);
            if (result.Outcome != CloudProtectedLocalOperationOutcome.Completed || !result.CallbackCompleted || !result.Drained ||
                changed && (!result.AccessDescriptorApplied || !result.AccessDescriptorReadBack))
                throw new IOException("The original KeepRemote file seal did not complete and drain; the retained action requires recovery.");
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore, await parentGuard.InspectAsync(token).ConfigureAwait(false));

            void RequireParentHandle(CancellationToken stop) => MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore,
                parentGuard.InspectRetainedDirectoryPermissions(parent.Member.LocalObject, stop));
        }).ConfigureAwait(false);
        try { await RequireOriginalsAsync(token, true).ConfigureAwait(false); }
        catch (MirrorPulseKeepRemoteJournalDeferredException error)
        {
            error.SealResult = result;
            throw;
        }
        return result;

        async ValueTask RequireOriginalsAsync(CancellationToken stop, bool allowSuccessors)
        {
            actor.AssertAccess();
            await store.RequireCurrentAsync(storeBinding, stop).ConfigureAwait(false);
            if (allowSuccessors)
                _ = await journal.RevalidateRetainedOriginalsAsync(copies, stop).ConfigureAwait(false);
            else
                _ = await journal.RevalidateRetainedAsync(copies, stop).ConfigureAwait(false);
        }
    }

    private static void RequireObserved(MirrorPulseNamespacePermissionObject observed,
        MirrorPulseNamespaceMemberProtectionSnapshot expected, string expectedDacl)
    {
        var member = expected.Context.Member;
        if (observed.LocalObject != member.LocalObject || observed.RootId != member.RootId || observed.IsDirectory != member.IsDirectory ||
            observed.OwnerSid != member.OwnerSid || observed.OwnerSid != expected.Context.Verification.OwnerSid || observed.LinkCount != 1 ||
            observed.RelativePath != expected.Context.Location.RelativePath ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(expectedDacl, observed.Dacl))
            throw new InvalidDataException("KeepRemote requires the retained single-link object, direct location, owner and permission policy.");
    }
}
