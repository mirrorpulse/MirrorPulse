using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseKeepRemoteRoleCheckpoint { AfterRoleStart, AfterMetadataHandoff, AfterRoleRecorded }

/// <summary>Hands the retained sealed resident file to a new Host role through owner metadata access.</summary>
/// <remarks>The caller excludes the previous Host and retains the instance scheduler, actor and pending root admission.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictKeepRemoteResidentRoleRecovery(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseKeepRemoteRoleCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    internal static MirrorPulseConflictKeepRemoteRoleAttempt? RequireCompletedHead(IReadOnlyList<MirrorPulseConflictKeepRemoteRoleAttempt> attempts)
    {
        if (attempts.Count == 0) return null;
        var head = attempts[^1];
        return head.Result is not null ? head : throw new InvalidOperationException("KeepRemote requires the current role's verified handoff.");
    }

    internal async Task<MirrorPulseConflictKeepRemoteRoleResult> RecoverAsync(RootId admittedRoot,
        MirrorPulseConflictKeepRemoteCopies copies, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copies);
        actor.AssertAccess();
        token.ThrowIfCancellationRequested();
        var intent = copies.Preparation.Intent;
        var first = await catalog.ReadConflictKeepRemoteFileStartAsync(intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Role recovery requires the original file start.");
        var action = await catalog.ReadConflictKeepRemoteFileActionAsync(intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Role recovery requires the original permission action.");
        var root = router.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey);
        if (copies.Local is null || action.OwnerSid != execution.OwnerSid.Value || first.Original.Context.Member.RootId != admittedRoot ||
            first.Parent.Context.Member.RootId != admittedRoot || root.RootId != admittedRoot || root.State != RootRegistrationState.Active)
            throw new InvalidOperationException("Role recovery requires the original owner, both private copies and the admitted active root.");

        var attempts = await catalog.ReadConflictKeepRemoteRoleAttemptsAsync(intent.CommandId, token).ConfigureAwait(false);
        var head = attempts.Count == 0 ? null : attempts[^1];
        string role = execution.RoleSid.Value;
        string sealedDacl = MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution);
        string restoredDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, isDirectory: false);
        if (role == action.RoleSid)
            throw new InvalidOperationException("The original role cannot enter a handoff to itself.");
        var file = fileSystem.GetFile(first.Original.Context.Location.RelativePath);
        var parent = fileSystem.GetDirectory(first.Parent.Context.Location.RelativePath);
        if (file.Parent is null || !string.Equals(file.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Role recovery lost the original direct parent.");

        using var store = await execution.RunNormalUserOperationAsync(() => state.OpenStoreFileLeaseAsync(token).AsTask()).ConfigureAwait(false);
        var storeBinding = new MirrorPulseCfSharpStoreFileBinding(first.StoreVolumeSerialNumber, first.StoreFileId);
        var journal = new MirrorPulseConflictKeepRemoteJournal(state.OpenStore, catalog, router, feed);
        await RequireOriginalsAsync(token).ConfigureAwait(false);
        if (head is not null && head.Start.NextAction.RoleSid == role && head.Result is { } retained)
            return retained;

        MirrorPulseConflictKeepRemoteRoleResult result = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            actor.AssertAccess();
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
            var parentBefore = await parentGuard.InspectAsync(token).ConfigureAwait(false);
            RequireParent(parentBefore);
            await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, token).ConfigureAwait(false);
            var before = await metadata.InspectAsync(token).ConfigureAwait(false);
            RequireChild(before);
            var identity = await ReadIdentityAsync(token).ConfigureAwait(false);
            string encoded = Convert.ToBase64String(identity.Encode());
            var start = head is not null && head.Start.NextAction.RoleSid == role ? head.Start : null;
            if (start is null)
            {
                var next = new MirrorPulseConflictKeepRemoteFileAction(1, intent.CommandId, Guid.NewGuid(), action.LocalObject,
                    action.OwnerSid, role, sealedDacl, restoredDacl, DateTimeOffset.UtcNow);
                start = await catalog.RecordConflictKeepRemoteRoleStartAsync(new(1, intent.CommandId, action.ActionId,
                    head?.Start.NextAction.ActionId, next, encoded, Verification(before), Verification(parentBefore), before.LinkCount!.Value), token).ConfigureAwait(false);
            }
            else if (start.PlaceholderIdentity != encoded || start.NextAction.RoleOnlyDacl != sealedDacl || start.NextAction.RestoreDacl != restoredDacl)
                throw new InvalidDataException("The original role handoff no longer matches this role or native identity.");

            await AtAsync(MirrorPulseKeepRemoteRoleCheckpoint.AfterRoleStart, token).ConfigureAwait(false);
            // Replay may find the result of the same metadata write. The owner-only helper accepts
            // only the observed prior descriptor and the freshly derived descriptor for this role.
            _ = await MirrorPulseWindowsNamespacePermissionLease.ApplyOwnedFileRoleHandoffAsync(file, router, action.LocalObject,
                action.OwnerSid, [start.Child.Dacl], execution, requireOrdinary: false, identity, token).ConfigureAwait(false);
            await AtAsync(MirrorPulseKeepRemoteRoleCheckpoint.AfterMetadataHandoff, CancellationToken.None).ConfigureAwait(false);
            var childAfter = await metadata.InspectAsync(CancellationToken.None).ConfigureAwait(false);
            var parentAfter = await parentGuard.InspectAsync(CancellationToken.None).ConfigureAwait(false);
            RequireChild(childAfter);
            RequireParent(parentAfter);
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(parentBefore, parentAfter);
            var nativeAfter = await ReadIdentityAsync(CancellationToken.None).ConfigureAwait(false);
            if (!identity.Equals(nativeAfter) || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(sealedDacl, childAfter.Dacl))
                throw new InvalidDataException("Role recovery lost the original placeholder or current role's verified descriptor.");
            await execution.RunNormalUserOperationAsync(() => RequireOriginalsAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            result = await catalog.RecordConflictKeepRemoteRoleResultAsync(new(1, intent.CommandId, action.ActionId, start.NextAction.ActionId,
                encoded, Verification(childAfter), Verification(parentAfter), childAfter.LinkCount!.Value, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            await AtAsync(MirrorPulseKeepRemoteRoleCheckpoint.AfterRoleRecorded, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return result;

        async ValueTask RequireOriginalsAsync(CancellationToken stop)
        {
            await store.RequireCurrentAsync(storeBinding, stop).ConfigureAwait(false);
            await journal.RevalidateRetainedOriginalsAsync(copies, stop).ConfigureAwait(false);
        }
        async ValueTask<CloudPlaceholderIdentity> ReadIdentityAsync(CancellationToken stop)
        {
            var originalIdentity = await MirrorPulseKeepRemotePublicIdentity.ReadOriginalAsync(catalog, root, intent, stop).ConfigureAwait(false);
            var native = await file.InspectAsync(stop).ConfigureAwait(false);
            if (!native.Exists || !native.IsPlaceholder || native.IsTombstone || native.Kind != CloudItemKind.File ||
                native.ContentAvailability != CloudContentAvailability.FullyAvailable || native.LocalBinding is not { } binding ||
                new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != action.LocalObject ||
                !CloudPlaceholderIdentity.TryDecode(native.PlaceholderIdentity.Span, out var identity) || identity is null ||
                identity.ItemId != originalIdentity.ItemId || identity.RemoteId != originalIdentity.RemoteId)
                throw new InvalidDataException("Role recovery requires the original fully resident placeholder and public identity.");
            return identity;
        }
        void RequireChild(MirrorPulseNamespacePermissionObject observed)
        {
            if (observed.LocalObject != action.LocalObject || observed.OwnerSid != action.OwnerSid || observed.IsDirectory || observed.LinkCount != 1 ||
                observed.RootId != admittedRoot || observed.RelativePath != first.Original.Context.Location.RelativePath)
                throw new InvalidDataException("Role recovery requires the original single-link child and owner.");
        }
        void RequireParent(MirrorPulseNamespacePermissionObject observed)
        {
            var expected = first.Parent.Context.Member;
            if (observed.LocalObject != expected.LocalObject || observed.OwnerSid != expected.OwnerSid || !observed.IsDirectory ||
                observed.LinkCount != 1 || observed.RootId != admittedRoot || observed.RelativePath != first.Parent.Context.Location.RelativePath ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(first.Parent.ExpectedDacl, observed.Dacl))
                throw new InvalidDataException("Role recovery requires the retained direct parent and its closed permissions.");
        }
    }

    private static MirrorPulseNamespacePermissionVerification Verification(MirrorPulseNamespacePermissionObject observed) =>
        new(observed.LocalObject, observed.OwnerSid, observed.Dacl, observed.ObservedAt);

    private ValueTask AtAsync(MirrorPulseKeepRemoteRoleCheckpoint checkpoint, CancellationToken token) =>
        Checkpoint?.Invoke(checkpoint, token) ?? ValueTask.CompletedTask;
}
