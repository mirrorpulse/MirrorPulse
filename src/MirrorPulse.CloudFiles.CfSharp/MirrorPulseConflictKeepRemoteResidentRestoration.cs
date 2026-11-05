using System.ComponentModel;
using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseKeepRemoteRestoreCheckpoint { AfterRestoreStart, AfterAccessApplied, AfterScopeReturned, AfterRestoreRecorded }

/// <summary>Restores the original file's access through a public same-object exclusive SDK scope.</summary>
/// <remarks>
/// The caller retains Host lifecycle, scheduler, actor, pending-root admission and both private
/// copies. Native confirmation must already have succeeded. This never recopies bytes, changes
/// identity, accepts a later edit, acknowledges originals or releases the command's fence.
/// Another execution role requires a separate retained recovery handoff.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictKeepRemoteResidentRestoration(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseKeepRemoteRestoreCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    internal async Task<MirrorPulseConflictKeepRemoteRestoreResult> RestoreAsync(RootId admittedRoot,
        MirrorPulseConflictKeepRemoteCopies copies, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copies); actor.AssertAccess(); token.ThrowIfCancellationRequested();
        var intent = copies.Preparation.Intent;
        var attempts = await catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(intent.CommandId, token).ConfigureAwait(false);
        if (attempts.Select(attempt => attempt.Result).FirstOrDefault(result => result?.ProductVerified == true) is { } historical)
            return historical;
        var first = await catalog.ReadConflictKeepRemoteFileStartAsync(intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Restoring access requires the original resident file start.");
        var content = await catalog.ReadConflictKeepRemoteFileContentAsync(intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Restoring access requires the complete original selected content.");
        var native = (await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(intent.CommandId, token).ConfigureAwait(false))
            .SingleOrDefault(attempt => attempt.Result?.Receipt.MayAcknowledge == true)
            ?? throw new InvalidOperationException("Restoring access requires the original successful public native confirmation.");
        var action = content.Action;
        var selectedIdentity = new CloudPlaceholderIdentity(intent.ItemId, intent.RemoteSource.RemoteId, intent.RemoteSource.RemoteRevision);
        string selected = Convert.ToBase64String(selectedIdentity.Encode());
        if (copies.Local is null || content.Completion?.Copy != copies.Remote.Proof ||
            first.Original.Context.Member.RootId != admittedRoot || first.Parent.Context.Member.RootId != admittedRoot ||
            action.RoleSid != execution.RoleSid.Value || action.OwnerSid != execution.OwnerSid.Value ||
            action.RoleOnlyDacl != MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution) ||
            action.RestoreDacl != MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(execution, isDirectory: false) ||
            native.Start.SelectedPlaceholderIdentity != selected)
            throw new InvalidOperationException("Restoring access requires the original complete copies, selected identity and current role.");
        var root = router.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey);
        if (root.RootId != admittedRoot || root.State != RootRegistrationState.Active)
            throw new InvalidOperationException("Restoring access requires the original admitted active root.");
        var file = fileSystem.GetFile(first.Original.Context.Location.RelativePath);
        var parent = fileSystem.GetDirectory(first.Parent.Context.Location.RelativePath);
        if (file.Parent is null || !string.Equals(file.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restoring access lost the original direct parent.");
        using var store = await execution.RunNormalUserOperationAsync(() => state.OpenStoreFileLeaseAsync(token).AsTask()).ConfigureAwait(false);
        var storeBinding = new MirrorPulseCfSharpStoreFileBinding(first.StoreVolumeSerialNumber, first.StoreFileId);
        var journal = new MirrorPulseConflictKeepRemoteJournal(state.OpenStore, catalog, router, feed);
        await RequireOriginalsAsync(token).ConfigureAwait(false);
        MirrorPulseConflictKeepRemoteRestoreResult result = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
            await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, token).ConfigureAwait(false);
            var before = await InspectAsync(token).ConfigureAwait(false);
            RequireChild(before.Child, restored: false); RequireParent(before.Parent); RequireIdentity(before.Snapshot);
            var start = await catalog.RecordConflictKeepRemoteRestoreStartAsync(new(1, intent.CommandId, action.ActionId,
                Guid.NewGuid(), native.Start.AttemptId, selected, before.Snapshot.SynchronizationState == CloudSynchronizationState.InSync,
                Verification(before.Child), Verification(before.Parent), DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            await AtAsync(MirrorPulseKeepRemoteRestoreCheckpoint.AfterRestoreStart, token).ConfigureAwait(false);
            var scope = await MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(file, router, action.LocalObject,
                async (lease, stop) =>
                {
                    actor.AssertAccess();
                    RequireChild(await lease.InspectAsync(stop).ConfigureAwait(false), restored: false);
                    await RequireScopeIdentityAsync(lease, stop).ConfigureAwait(false);
                    RequireParent(parentGuard.InspectRetainedDirectoryPermissions(first.Parent.Context.Member.LocalObject, stop));
                    // Always reapply on an uncertain return so this public operation supplies
                    // its own actual application/readback facts on the original object.
                    await lease.ApplyDaclAsync(action.RestoreDacl, stop).ConfigureAwait(false);
                    await AtAsync(MirrorPulseKeepRemoteRestoreCheckpoint.AfterAccessApplied, stop).ConfigureAwait(false);
                    RequireChild(await lease.InspectAsync(stop).ConfigureAwait(false), restored: true);
                    await RequireScopeIdentityAsync(lease, stop).ConfigureAwait(false);
                    RequireParent(parentGuard.InspectRetainedDirectoryPermissions(first.Parent.Context.Member.LocalObject, stop));
                }, token).ConfigureAwait(false);
            await AtAsync(MirrorPulseKeepRemoteRestoreCheckpoint.AfterScopeReturned, CancellationToken.None).ConfigureAwait(false);
            var binding = scope.ExpectedBinding;
            var receipt = new MirrorPulseConflictKeepRemoteRestoreReceipt(scope.Outcome.ToString(), scope.Stage.ToString(),
                new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId), scope.CallbackStarted, scope.CallbackCompleted,
                scope.AccessDescriptorApplied, scope.AccessDescriptorReadBack, scope.Drained, scope.NativeIdentityPrepared,
                scope.NativeConverted, scope.DurableProjectionCommitted, scope.PreparationHResult, scope.PreparationUsn,
                scope.Error?.HResult, scope.CancellationRequested, scope.BudgetExpired, DateTimeOffset.UtcNow);
            MirrorPulseNamespacePermissionVerification? child = null, parentProof = null;
            uint? links = null; string? identity = null; bool? inSync = null; bool verified = false;
            if (scope.Drained)
            {
                try
                {
                    var after = await InspectAsync(CancellationToken.None).ConfigureAwait(false);
                    child = Verification(after.Child); parentProof = Verification(after.Parent); links = after.Child.LinkCount;
                    identity = after.Snapshot.IsPlaceholder ? Convert.ToBase64String(after.Snapshot.PlaceholderIdentity.Span) : null;
                    inSync = after.Snapshot.SynchronizationState == CloudSynchronizationState.InSync;
                    RequireChild(after.Child, restored: true); RequireParent(after.Parent); RequireIdentity(after.Snapshot);
                    // Stage is the last work phase, not CurrentStage's released lifecycle.
                    verified = scope.Outcome == CloudProtectedLocalOperationOutcome.Completed && scope.Stage == CloudProtectedLocalOperationStage.Inspection &&
                        scope.CallbackStarted && scope.CallbackCompleted && scope.AccessDescriptorApplied && scope.AccessDescriptorReadBack && scope.Error is null &&
                        !scope.NativeIdentityPrepared && !scope.NativeConverted && !scope.DurableProjectionCommitted;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
                {
                    // Preserve the actual SDK result and any independent observations. An
                    // unverified result retains both private copies and the original fence.
                    verified = false;
                }
            }
            result = await catalog.RecordConflictKeepRemoteRestoreResultAsync(new(1, intent.CommandId, action.ActionId, start.AttemptId,
                native.Start.AttemptId, receipt, child, parentProof, links, identity, inSync, verified, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            await AtAsync(MirrorPulseKeepRemoteRestoreCheckpoint.AfterRestoreRecorded, CancellationToken.None).ConfigureAwait(false);

            async Task<(MirrorPulseNamespacePermissionObject Child, MirrorPulseNamespacePermissionObject Parent, CloudItemSnapshot Snapshot)> InspectAsync(CancellationToken stop) =>
                (await metadata.InspectAsync(stop).ConfigureAwait(false), await parentGuard.InspectAsync(stop).ConfigureAwait(false),
                    await file.InspectAsync(stop).ConfigureAwait(false));
        }).ConfigureAwait(false);
        await RequireOriginalsAsync(CancellationToken.None).ConfigureAwait(false);
        return result;

        async ValueTask RequireOriginalsAsync(CancellationToken stop)
        {
            await store.RequireCurrentAsync(storeBinding, stop).ConfigureAwait(false);
            await journal.RevalidateRetainedOriginalsAsync(copies, stop).ConfigureAwait(false);
        }
        void RequireChild(MirrorPulseNamespacePermissionObject observed, bool restored)
        {
            if (observed.LocalObject != action.LocalObject || observed.OwnerSid != action.OwnerSid || observed.IsDirectory || observed.LinkCount != 1 ||
                observed.RootId != admittedRoot || observed.RelativePath != file.RelativePath.Replace('\\', '/') ||
                !(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RestoreDacl, observed.Dacl) && (restored || attempts.Count > 0) ||
                  !restored && MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, observed.Dacl)))
                throw new InvalidDataException("Access restoration requires the original single-link file and its retained permission history.");
        }
        void RequireParent(MirrorPulseNamespacePermissionObject observed)
        {
            if (observed.LocalObject != first.Parent.Context.Member.LocalObject || observed.OwnerSid != first.Parent.Context.Member.OwnerSid ||
                !observed.IsDirectory || observed.LinkCount != 1 || observed.RootId != admittedRoot ||
                observed.RelativePath != parent.RelativePath.Replace('\\', '/') ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(first.Parent.ExpectedDacl, observed.Dacl))
                throw new InvalidDataException("Access restoration requires the current original direct-parent protections.");
        }
        void RequireIdentity(CloudItemSnapshot observed)
        {
            if (!observed.Exists || !observed.IsPlaceholder || observed.IsTombstone || observed.Kind != CloudItemKind.File ||
                observed.ContentAvailability != CloudContentAvailability.FullyAvailable || observed.LocalBinding is not { } binding ||
                new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != action.LocalObject ||
                !observed.PlaceholderIdentity.Span.SequenceEqual(selectedIdentity.Encode()))
                throw new InvalidDataException("Access restoration requires the original resident object and retained selected native identity.");
        }
        async ValueTask RequireScopeIdentityAsync(IMirrorPulseProtectedNamespacePermissionLease lease, CancellationToken stop)
        {
            var observed = await lease.InspectLocalIdentityAsync(stop).ConfigureAwait(false);
            if (!observed.IsPlaceholder || observed.ItemId != intent.ItemId || observed.RemoteId != intent.RemoteSource.RemoteId ||
                !selectedIdentity.Equals(observed.PlaceholderIdentity))
                throw new InvalidDataException("The protected access callback lost its selected original identity.");
        }
    }

    private static MirrorPulseNamespacePermissionVerification Verification(MirrorPulseNamespacePermissionObject observed) =>
        new(observed.LocalObject, observed.OwnerSid, observed.Dacl, observed.ObservedAt);

    private ValueTask AtAsync(MirrorPulseKeepRemoteRestoreCheckpoint point, CancellationToken stop) =>
        Checkpoint?.Invoke(point, stop) ?? ValueTask.CompletedTask;
}
