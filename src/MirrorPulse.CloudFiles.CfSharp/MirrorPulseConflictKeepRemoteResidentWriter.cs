using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseKeepRemoteContentCheckpoint { AfterWriteStart, AfterContentFlush, AfterContentRecorded }

/// <summary>Persists complete selected remote bytes under the original resident file's content seal.</summary>
/// <remarks>The caller retains Host lifecycle, instance scheduler, actor and pending root admission.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictKeepRemoteResidentWriter(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseKeepRemoteContentCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    internal async Task<MirrorPulseConflictKeepRemoteFileContentCompletion> CopyAsync(RootId admittedRoot,
        MirrorPulseConflictKeepRemoteCopies copies, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copies);
        actor.AssertAccess(); token.ThrowIfCancellationRequested();
        Guid commandId = copies.Preparation.Intent.CommandId;
        var history = await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token).ConfigureAwait(false);
        if (history?.Completion is { } completed) return completed;
        await new MirrorPulseConflictKeepRemoteResidentSeal(fileSystem, catalog, state, feed, router, execution, actor)
            .ApplyFirstAsync(admittedRoot, copies, token).ConfigureAwait(false);
        var first = await catalog.ReadConflictKeepRemoteFileStartAsync(commandId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote file start disappeared.");
        history = await catalog.ReadConflictKeepRemoteFileContentAsync(commandId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote permission action disappeared.");
        var action = history.Action;
        if (action.RoleSid != execution.RoleSid.Value || action.OwnerSid != execution.OwnerSid.Value ||
            first.Original.Context.Member.RootId != admittedRoot || first.Parent.Context.Member.RootId != admittedRoot ||
            copies.Local is null || action.RoleOnlyDacl != MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution))
            throw new InvalidOperationException("Resident copying requires the original active permission action and both retained private copies.");
        string identity = copies.Preparation.Intent.LocalContent!.UploadBinding.PreviousPlaceholderIdentity!;
        var file = fileSystem.GetFile(first.Original.Context.Location.RelativePath);
        var parent = fileSystem.GetDirectory(first.Parent.Context.Location.RelativePath);
        var journal = new MirrorPulseConflictKeepRemoteJournal(state.OpenStore, catalog, router, feed);
        using var store = await execution.RunNormalUserOperationAsync(() => state.OpenStoreFileLeaseAsync(token).AsTask()).ConfigureAwait(false);
        var storeBinding = new MirrorPulseCfSharpStoreFileBinding(first.StoreVolumeSerialNumber, first.StoreFileId);
        await RequireOriginalsAsync(token).ConfigureAwait(false);
        MirrorPulseConflictKeepRemoteFileContentCompletion result = null!;
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(parent, router, token).ConfigureAwait(false);
            await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, token).ConfigureAwait(false);
            await using var target = new FileStream(file.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous);
            var write = history.WriteStart;
            await InspectAsync(token).ConfigureAwait(false);
            if (write is null)
            {
                target.Position = 0;
                string localHash = Convert.ToHexString(await SHA256.HashDataAsync(target, token).ConfigureAwait(false));
                if (target.Length != copies.Local.Proof.Length || localHash != copies.Local.Proof.Sha256)
                    throw new InvalidDataException("The latest local bytes differ from their selected complete private preservation.");
            }
            string hash = await MirrorPulseConflictKeepRemoteContentTransfer.CopyResidentAsync(copies.Remote.Data, copies.Remote.Proof,
                target, async stop =>
                {
                    var before = await InspectAsync(stop).ConfigureAwait(false);
                    if (write is null)
                        write = await catalog.RecordConflictKeepRemoteFileWriteStartAsync(new(1, commandId, action.ActionId,
                            Verification(before.Child), Verification(before.Parent), before.Child.LinkCount!.Value,
                            identity, DateTimeOffset.UtcNow), stop).ConfigureAwait(false);
                    await AtCheckpointAsync(MirrorPulseKeepRemoteContentCheckpoint.AfterWriteStart, stop).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            await AtCheckpointAsync(MirrorPulseKeepRemoteContentCheckpoint.AfterContentFlush, token).ConfigureAwait(false);
            var after = await InspectAsync(CancellationToken.None).ConfigureAwait(false);
            if (hash != copies.Remote.Proof.Sha256 || target.Length != copies.Remote.Proof.Length)
                throw new InvalidDataException("The complete resident KeepRemote bytes differ from the selected private version.");
            result = await catalog.RecordConflictKeepRemoteFileContentCompletionAsync(new(1, commandId, action.ActionId, copies.Remote.Proof,
                Verification(after.Child), Verification(after.Parent), after.Child.LinkCount!.Value, identity, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            await AtCheckpointAsync(MirrorPulseKeepRemoteContentCheckpoint.AfterContentRecorded, token).ConfigureAwait(false);

            async ValueTask<(MirrorPulseNamespacePermissionObject Child, MirrorPulseNamespacePermissionObject Parent)> InspectAsync(CancellationToken stop)
            {
                actor.AssertAccess();
                MirrorPulseWindowsNamespacePermissionLease.RequireRetainedObject(target.SafeFileHandle, action.LocalObject);
                var child = await metadata.InspectAsync(stop).ConfigureAwait(false);
                var parentNow = await parentGuard.InspectAsync(stop).ConfigureAwait(false);
                var native = await file.InspectAsync(stop).ConfigureAwait(false);
                if (child.LocalObject != action.LocalObject || child.OwnerSid != action.OwnerSid || child.LinkCount != 1 || child.IsDirectory ||
                    child.RootId != admittedRoot || child.RelativePath != file.RelativePath.Replace('\\', '/') ||
                    !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, child.Dacl) ||
                    parentNow.LocalObject != first.Parent.Context.Member.LocalObject || parentNow.RootId != admittedRoot ||
                    !parentNow.IsDirectory || parentNow.OwnerSid != first.Parent.Context.Member.OwnerSid || parentNow.LinkCount != 1 ||
                    !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(first.Parent.ExpectedDacl, parentNow.Dacl) ||
                    !native.Exists || !native.IsPlaceholder || native.IsTombstone || native.ContentAvailability != CloudContentAvailability.FullyAvailable ||
                    native.LocalBinding is not { } binding || new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber,
                        binding.SyncRootFileId, binding.LocalFileId) != action.LocalObject ||
                    Convert.ToBase64String(native.PlaceholderIdentity.Span) != identity)
                    throw new InvalidDataException("Resident copying requires the same sealed placeholder, direct parent, current permissions and complete local availability.");
                return (child, parentNow);
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

    private ValueTask AtCheckpointAsync(MirrorPulseKeepRemoteContentCheckpoint checkpoint, CancellationToken token) =>
        Checkpoint?.Invoke(checkpoint, token) ?? ValueTask.CompletedTask;
}
