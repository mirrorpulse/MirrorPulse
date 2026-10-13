using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseNamespaceReplacementContentCheckpoint { AfterBackupRecorded, AfterWriteStarted, AfterContentFlush, AfterContentRecorded }
internal sealed record MirrorPulseNamespaceReplacementWriteObservation(MirrorPulseNamespacePermissionObject Object,
    bool IsPlaceholder, bool IsInSync, string? PlaceholderIdentity);

/// <summary>Copies verified new content onto an admitted, retained role-only existing file.</summary>
/// <remarks>
/// The caller owns the instance scheduler, namespace role, actor and permission-tightening action.
/// Open verified private input as the normal user before acquiring those boundaries. Cold content
/// must already be fully resident; this writer never hydrates, accesses a source, converts, restores
/// access, releases a root fence, accepts remote content or acknowledges journal IDs.
/// A retained copy receipt returns history without authorizing another write.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceReplacementContentWriter(MirrorPulseProductCatalog catalog,
    MirrorPulseLocalReplacementBackupStore backups, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseNamespaceReplacementContentCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    public async Task<MirrorPulseNamespaceReplacementContentCompletion> CopyAsync(Guid operationId, CloudFile file,
        CloudDirectory parent, MirrorPulseLocalImportInputLease? input, CancellationToken cancellationToken = default)
    {
        actor.AssertAccess(); ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(parent);
        var retained = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original replacement admission is missing.");
        if (retained.ContentCompletion is not null) return retained.ContentCompletion;
        var action = RequireCurrentAction(retained);
        var intent = retained.Intent;
        if (action.RoleSid != execution.RoleSid.Value || action.OwnerSid != execution.OwnerSid.Value ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(
                MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution), action.RoleOnlyDacl) ||
            file.RelativePath.Replace('\\', '/') != intent.Original.Context.Location.RelativePath || file.Parent is null ||
            !string.Equals(file.Parent.FullPath, parent.FullPath, StringComparison.OrdinalIgnoreCase) ||
            !CloudPlaceholderIdentity.TryDecode(Convert.FromBase64String(intent.OriginalPlaceholderIdentity), out var identity) ||
            identity is null || identity.ItemId != intent.Original.Identity.ItemId || identity.RemoteId != intent.Original.Identity.RemoteId)
            throw new InvalidOperationException("Copying requires the original file, public identity and current namespace role's exact policy.");
        var parentProof = await ReadCurrentParentAsync(catalog, intent, cancellationToken).ConfigureAwait(false);
        if (!parentProof.Context.Member.IsDirectory ||
            parent.RelativePath.Replace('\\', '/') != parentProof.Context.Location.RelativePath)
            throw new InvalidDataException("The original parent protection or current role changed before replacement copying.");
        await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, cancellationToken).ConfigureAwait(false);
        var before = await parentGuard.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (before.LocalObject != parentProof.Context.Member.LocalObject || before.OwnerSid != parentProof.Context.Verification.OwnerSid ||
            !before.IsDirectory || !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parentProof.ExpectedDacl, before.Dacl))
            throw new InvalidDataException("The retained parent lost its admitted original binding, owner or protection.");
        await using var metadata = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, cancellationToken).ConfigureAwait(false);
        _ = await InspectAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(file.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await CopyRetainedAsync(catalog, backups, operationId, target, input, InspectAsync, Checkpoint, cancellationToken).ConfigureAwait(false);

        async ValueTask<MirrorPulseNamespaceReplacementWriteObservation> InspectAsync(CancellationToken stop)
        {
            var observed = await metadata.InspectAsync(stop).ConfigureAwait(false);
            var native = await file.InspectAsync(stop).ConfigureAwait(false);
            if (!native.Exists || native.IsTombstone || native.Kind != CloudItemKind.File || native.LocalBinding is not { } binding ||
                observed.LocalObject != new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) ||
                native.IsPlaceholder && (native.ContentAvailability != CloudContentAvailability.FullyAvailable ||
                    native.SynchronizationState is not (CloudSynchronizationState.InSync or CloudSynchronizationState.NotInSync)))
                throw new InvalidDataException("Replacement copying requires the retained original fully resident file without source recall.");
            MirrorPulseNamespaceBirthProtectionVerifier.RequireUnchangedParent(before, await parentGuard.InspectAsync(stop).ConfigureAwait(false));
            actor.AssertAccess();
            return new(observed, native.IsPlaceholder, native.SynchronizationState == CloudSynchronizationState.InSync,
                native.IsPlaceholder ? Convert.ToBase64String(native.PlaceholderIdentity.Span) : null);
        }
    }

    internal static async Task<MirrorPulseNamespaceMemberProtectionSnapshot> ReadCurrentParentAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceReplacementIntent intent, CancellationToken cancellationToken)
    {
        // Registration retains the first parent. Only the committed product location selects
        // the currently guarded parent after a completed same-root member or ancestor move.
        Guid parentId = intent.Original.Context.Location.ParentMemberId
            ?? throw new InvalidDataException("The admitted existing file has no current managed parent.");
        var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(parentId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The admitted current parent protection is missing.");
        if (!parent.Context.Member.IsDirectory || parent.Context.Member.RootId != intent.RootId)
            throw new InvalidDataException("The current replacement parent is outside the admitted managed root.");
        return parent;
    }

    internal static MirrorPulseNamespaceReplacementAction RequireCurrentAction(MirrorPulseNamespaceReplacementExecution facts)
    {
        var first = facts.Action ?? throw new InvalidOperationException("Replacement requires its immutable first permission action.");
        if (facts.RoleActions.Count == 0) return first;
        var current = facts.RoleActions[^1];
        if (facts.RoleHandoff?.RecoveryId != current.RecoveryId)
            throw new InvalidOperationException("Replacement requires the current role's independently recorded original-object handoff.");
        return current.Action;
    }

    /// <summary>Byte persistence boundary; the native caller separately retains and freshly checks original object and parent guards.</summary>
    internal static async Task<MirrorPulseNamespaceReplacementContentCompletion> CopyRetainedAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseLocalReplacementBackupStore backups, Guid operationId, FileStream? target, MirrorPulseLocalImportInputLease? input,
        Func<CancellationToken, ValueTask<MirrorPulseNamespaceReplacementWriteObservation>> inspect,
        Func<MirrorPulseNamespaceReplacementContentCheckpoint, CancellationToken, ValueTask>? checkpoint,
        CancellationToken cancellationToken)
    {
        var retained = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original replacement admission is missing.");
        if (retained.ContentCompletion is not null) return retained.ContentCompletion;
        var action = RequireCurrentAction(retained);
        Guid firstActionId = retained.Action!.ActionId;
        Guid? authorityId = retained.RoleActions.Count == 0 ? null : action.ActionId;
        if (target is null || !target.CanRead || !target.CanWrite || !target.CanSeek || input is null ||
            input.OperationId != operationId || input.Content != retained.Intent.Content)
            throw new InvalidOperationException("Replacement copying requires its retained read-write object and verified original input lease.");
        ArgumentNullException.ThrowIfNull(inspect); cancellationToken.ThrowIfCancellationRequested();
        RequireRoleOnly(await inspect(cancellationToken).ConfigureAwait(false));
        var backup = retained.Backup;
        if (backup is null)
        {
            var oldContent = await backups.StageRetainedAsync(operationId, target, cancellationToken).ConfigureAwait(false);
            var observed = await inspect(cancellationToken).ConfigureAwait(false); RequireRoleOnly(observed);
            backup = await catalog.RecordNamespaceReplacementBackupAsync(new(authorityId is null ? 1 : 2, operationId, firstActionId, oldContent,
                new(observed.Object.LocalObject, observed.Object.OwnerSid, observed.Object.Dacl, observed.Object.ObservedAt),
                observed.Object.LinkCount!.Value)
            { AuthorityRecoveryId = authorityId }, CancellationToken.None).ConfigureAwait(false);
        }
        // Retain and independently verify the first old backup even after an interrupted write.
        await using var oldBytes = await backups.OpenLeaseAsync(operationId, backup.Content, cancellationToken).ConfigureAwait(false);
        CancellationToken durable = CancellationToken.None;
        await CheckpointAsync(MirrorPulseNamespaceReplacementContentCheckpoint.AfterBackupRecorded).ConfigureAwait(false);
        if (retained.WriteStart is null)
            _ = await catalog.RecordNamespaceReplacementWriteStartAsync(new(1, operationId, firstActionId, DateTimeOffset.UtcNow), durable).ConfigureAwait(false);
        await CheckpointAsync(MirrorPulseNamespaceReplacementContentCheckpoint.AfterWriteStarted).ConfigureAwait(false);
        RequireRoleOnly(await inspect(durable).ConfigureAwait(false));
        bool alreadyContainsInput = false;
        if (backup.Content == retained.Intent.Content && target.Length == retained.Intent.Content.Length)
        {
            target.Position = 0;
            alreadyContainsInput = Convert.ToHexString(await SHA256.HashDataAsync(target, durable).ConfigureAwait(false)) == retained.Intent.Content.Sha256;
        }
        if (!alreadyContainsInput)
        {
            target.SetLength(0); target.Position = 0; input.Data.Position = 0;
            await input.Data.CopyToAsync(target, 64 * 1024, durable).ConfigureAwait(false);
        }
        await target.FlushAsync(durable).ConfigureAwait(false); target.Flush(flushToDisk: true); target.Position = 0;
        string digest = Convert.ToHexString(await SHA256.HashDataAsync(target, durable).ConfigureAwait(false));
        if (target.Length != retained.Intent.Content.Length || digest != retained.Intent.Content.Sha256)
            throw new InvalidDataException("The flushed original replacement object does not contain the complete admitted new bytes.");
        await CheckpointAsync(MirrorPulseNamespaceReplacementContentCheckpoint.AfterContentFlush).ConfigureAwait(false);
        var final = await inspect(durable).ConfigureAwait(false); RequireRoleOnly(final);
        if (final.IsInSync && backup.Content != retained.Intent.Content)
            throw new InvalidDataException("Changed local content must remain dirty before remote acceptance.");
        var completion = await catalog.RecordNamespaceReplacementContentCompletionAsync(new(authorityId is null ? 1 : 2, operationId, firstActionId,
            retained.Intent.Content, new(final.Object.LocalObject, final.Object.OwnerSid, final.Object.Dacl, final.Object.ObservedAt),
            final.Object.LinkCount!.Value, final.IsPlaceholder, final.IsInSync, final.PlaceholderIdentity)
        { AuthorityRecoveryId = authorityId }, durable).ConfigureAwait(false);
        await CheckpointAsync(MirrorPulseNamespaceReplacementContentCheckpoint.AfterContentRecorded).ConfigureAwait(false);
        return completion;

        ValueTask CheckpointAsync(MirrorPulseNamespaceReplacementContentCheckpoint point) => checkpoint?.Invoke(point, durable) ?? ValueTask.CompletedTask;

        void RequireRoleOnly(MirrorPulseNamespaceReplacementWriteObservation observation)
        {
            var facts = observation.Object;
            if (facts.LocalObject != action.LocalObject || facts.OwnerSid != action.OwnerSid || facts.IsDirectory || facts.LinkCount != 1 ||
                facts.RootId != retained.Intent.RootId || facts.RelativePath != retained.Intent.Original.Context.Location.RelativePath ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(action.RoleOnlyDacl, facts.Dacl) ||
                (observation.IsPlaceholder ? observation.PlaceholderIdentity != retained.Intent.OriginalPlaceholderIdentity : observation.PlaceholderIdentity is not null))
                throw new InvalidDataException("Copying lost the admitted single-link original object or its role-only protection and identity.");
        }
    }
}
