using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseKeepRemoteNativeCheckpoint { AfterNativeStart, AfterSdkReturned, AfterNativeRecorded }

/// <summary>Confirms the selected remote version through the public SDK, without restoring access or acknowledging changes.</summary>
/// <remarks>The caller retains Host lifecycle, instance scheduler, actor and pending root admission.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictKeepRemoteResidentConfirmation(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseKeepRemoteNativeCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    internal async Task<MirrorPulseConflictKeepRemoteNativeResult> ConfirmAsync(RootId admittedRoot,
        MirrorPulseConflictKeepRemoteCopies copies, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copies); actor.AssertAccess(); token.ThrowIfCancellationRequested();
        var intent = copies.Preparation.Intent;
        var attempts = await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(intent.CommandId, token).ConfigureAwait(false);
        // This is a past result, never evidence that today's bytes are still synchronized.
        if (attempts.Select(attempt => attempt.Result).FirstOrDefault(result => result?.Receipt.MayAcknowledge == true) is { } completed)
            return completed;
        var first = await catalog.ReadConflictKeepRemoteFileStartAsync(intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Native confirmation requires the original file start.");
        var content = await catalog.ReadConflictKeepRemoteFileContentAsync(intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Native confirmation requires the original content action.");
        var action = content.Action;
        if (content.Completion is null || copies.Local is null || content.Completion.Copy != copies.Remote.Proof ||
            first.Original.Context.Member.RootId != admittedRoot || first.Parent.Context.Member.RootId != admittedRoot ||
            action.RoleSid != execution.RoleSid.Value || action.OwnerSid != execution.OwnerSid.Value ||
            action.RoleOnlyDacl != MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution))
            throw new InvalidOperationException("Native confirmation requires the retained complete bytes, original root and current execution role.");
        var registration = router.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey);
        if (registration.RootId != admittedRoot || registration.State != RootRegistrationState.Active)
            throw new InvalidOperationException("Native confirmation requires the original active root.");
        var selectedIdentity = new CloudPlaceholderIdentity(intent.ItemId, intent.RemoteSource.RemoteId, intent.RemoteSource.RemoteRevision);
        string selected = Convert.ToBase64String(selectedIdentity.Encode());
        string original = content.Completion.OriginalPlaceholderIdentity;
        var binding = action.LocalObject;
        var file = fileSystem.GetFile(first.Original.Context.Location.RelativePath);
        var parent = fileSystem.GetDirectory(first.Parent.Context.Location.RelativePath);
        if (file.Parent is null || !string.Equals(file.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native confirmation lost the original direct parent.");
        using var store = await execution.RunNormalUserOperationAsync(() => state.OpenStoreFileLeaseAsync(token).AsTask()).ConfigureAwait(false);
        var storeBinding = new MirrorPulseCfSharpStoreFileBinding(first.StoreVolumeSerialNumber, first.StoreFileId);
        var journal = new MirrorPulseConflictKeepRemoteJournal(state.OpenStore, catalog, router, feed);
        await RequireOriginalsAsync(token).ConfigureAwait(false);
        MirrorPulseConflictKeepRemoteNativeResult result = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
            await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, token).ConfigureAwait(false);
            var before = await InspectAsync(token).ConfigureAwait(false);
            if (before.Identity != original && (attempts.Count == 0 || before.Identity != selected))
                throw new InvalidDataException("The current native identity cannot be adopted without the original before-native history.");
            var start = await catalog.RecordConflictKeepRemoteNativeStartAsync(new(1, intent.CommandId, action.ActionId, Guid.NewGuid(),
                selected, before.Identity, Verification(before.Child), Verification(before.Parent), DateTimeOffset.UtcNow), token).ConfigureAwait(false);
            await AtCheckpointAsync(MirrorPulseKeepRemoteNativeCheckpoint.AfterNativeStart, token).ConfigureAwait(false);
            var sdk = await file.ConfirmUploadedContentAsync(new(new(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId),
                selectedIdentity, copies.Remote.Proof.Length, Convert.FromHexString(copies.Remote.Proof.Sha256),
                CloudContentPreparation.ReplacePlaceholderIdentity, Convert.FromBase64String(original)), token).ConfigureAwait(false);
            var receipt = MirrorPulseContentConfirmation.MapReceipt(sdk);
            await AtCheckpointAsync(MirrorPulseKeepRemoteNativeCheckpoint.AfterSdkReturned, token).ConfigureAwait(false);
            var after = await InspectAsync(CancellationToken.None).ConfigureAwait(false);
            result = await catalog.RecordConflictKeepRemoteNativeResultAsync(new(1, intent.CommandId, action.ActionId, start.AttemptId,
                receipt, Verification(after.Child), Verification(after.Parent), DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
            await AtCheckpointAsync(MirrorPulseKeepRemoteNativeCheckpoint.AfterNativeRecorded, token).ConfigureAwait(false);

            async ValueTask<(MirrorPulseNamespacePermissionObject Child, MirrorPulseNamespacePermissionObject Parent, string Identity)> InspectAsync(CancellationToken stop)
            {
                actor.AssertAccess();
                var child = await metadata.InspectAsync(stop).ConfigureAwait(false);
                var parentNow = await parentGuard.InspectAsync(stop).ConfigureAwait(false);
                var native = await file.InspectAsync(stop).ConfigureAwait(false);
                if (child.LocalObject != binding || child.OwnerSid != action.OwnerSid || child.LinkCount != 1 || child.IsDirectory ||
                    child.RootId != admittedRoot || child.RelativePath != file.RelativePath.Replace('\\', '/') ||
                    !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, child.Dacl) ||
                    parentNow.LocalObject != first.Parent.Context.Member.LocalObject || parentNow.RootId != admittedRoot ||
                    parentNow.OwnerSid != first.Parent.Context.Member.OwnerSid || !parentNow.IsDirectory || parentNow.LinkCount != 1 ||
                    !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(first.Parent.ExpectedDacl, parentNow.Dacl) ||
                    !native.Exists || !native.IsPlaceholder || native.IsTombstone || native.ContentAvailability != CloudContentAvailability.FullyAvailable ||
                    native.LocalBinding is not { } actual || new MirrorPulseLocalFileBinding(actual.VolumeSerialNumber,
                        actual.SyncRootFileId, actual.LocalFileId) != binding)
                    throw new InvalidDataException("Native confirmation requires the same fully resident sealed file and current direct-parent protections.");
                string identity = Convert.ToBase64String(native.PlaceholderIdentity.Span);
                if (identity != original && identity != selected)
                    throw new InvalidDataException("The original or retained selected native identity is required.");
                return (child, parentNow, identity);
            }
        }).ConfigureAwait(false);
        await RequireOriginalsAsync(CancellationToken.None).ConfigureAwait(false);
        return result;

        async ValueTask RequireOriginalsAsync(CancellationToken stop)
        {
            await store.RequireCurrentAsync(storeBinding, stop).ConfigureAwait(false);
            await journal.RevalidateRetainedOriginalsAsync(copies, stop).ConfigureAwait(false);
        }
    }

    private static MirrorPulseNamespacePermissionVerification Verification(MirrorPulseNamespacePermissionObject observed) =>
        new(observed.LocalObject, observed.OwnerSid, observed.Dacl, observed.ObservedAt);

    private ValueTask AtCheckpointAsync(MirrorPulseKeepRemoteNativeCheckpoint checkpoint, CancellationToken token) =>
        Checkpoint?.Invoke(checkpoint, token) ?? ValueTask.CompletedTask;
}
