using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Executes an admitted replacement with the Host's shared instance scheduler and namespace actor.</summary>
/// <remarks>Admission, first action and target materialization precede execution. Private input opens as the normal user before scheduling. No source access or remote acceptance belongs here.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceReplacementExecutor(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor,
    MirrorPulseInstanceScheduler scheduler, MirrorPulseLocalImportStagingStore inputs, MirrorPulseLocalReplacementBackupStore backups)
{
    public async ValueTask<MirrorPulseNamespaceReplacementCompletion> ExecuteAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A replacement operation ID is required.", nameof(operationId));
        var completed = await catalog.ReadNamespaceReplacementCompletionAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (completed is not null) return completed;
        var facts = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original replacement admission is missing.");
        if (facts.Cancellation is not null || facts.Start is null || facts.Action is null)
            throw new InvalidOperationException("Execution requires the retained before-write start and first action.");
        var root = router.Registrations.SingleOrDefault(candidate => candidate.RootId == facts.Intent.RootId)
            ?? throw new FileNotFoundException("The admitted root is no longer registered.");
        MirrorPulseLocalImportInputLease? input = null;
        try
        {
            if (facts.ContentCompletion is null)
                input = await execution.RunNormalUserOperationAsync(async () =>
                    await inputs.OpenLeaseAsync(operationId, facts.Intent.Content, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            return await scheduler.RunAsync(root.InstanceId, token => actor.RunAsync(async stop =>
            {
                MirrorPulseNamespaceReplacementCompletion result = null!;
                await execution.RunNamespaceOperationAsync(async () =>
                    result = await ExecuteAdmittedAsync(operationId, input, stop).ConfigureAwait(false)).ConfigureAwait(false);
                return result;
            }, token), cancellationToken).ConfigureAwait(false);
        }
        finally { if (input is not null) await input.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task<MirrorPulseNamespaceReplacementCompletion> ExecuteAdmittedAsync(Guid operationId,
        MirrorPulseLocalImportInputLease? input, CancellationToken token)
    {
        actor.AssertAccess();
        var completed = await catalog.ReadNamespaceReplacementCompletionAsync(operationId, token).ConfigureAwait(false);
        if (completed is not null) return completed;
        var facts = await catalog.ReadNamespaceReplacementExecutionAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original replacement execution disappeared.");
        var parentProof = await MirrorPulseNamespaceReplacementContentWriter.ReadCurrentParentAsync(catalog, facts.Intent, token).ConfigureAwait(false);
        if (parentProof.Context.Protection.RoleSid != execution.RoleSid.Value || facts.Intent.Original.Context.Member.OwnerSid != execution.OwnerSid.Value)
            throw new InvalidOperationException("Replacement execution requires the actual current-role protected parent and original owner.");
        string path = facts.Intent.Original.Context.Location.RelativePath;
        var file = fileSystem.GetFile(path); var parent = fileSystem.GetDirectory(parentProof.Context.Location.RelativePath);
        if (facts.ContentCompletion is null)
        {
            var resident = await file.InspectAsync(token).ConfigureAwait(false);
            if (!resident.Exists || resident.IsTombstone || resident.IsPlaceholder && resident.ContentAvailability != CloudContentAvailability.FullyAvailable)
                throw new InvalidDataException("Replacement preparation must materialize the original before scheduled execution.");
        }
        if (!CloudPlaceholderIdentity.TryDecode(Convert.FromBase64String(facts.Intent.OriginalPlaceholderIdentity), out var identity) || identity is null ||
            identity.ItemId != facts.Intent.Original.Identity.ItemId || identity.RemoteId != facts.Intent.Original.Identity.RemoteId)
            throw new InvalidDataException("The original public replacement identity is inconsistent.");
        var first = facts.Action ?? throw new InvalidOperationException("The first replacement action is missing.");
        var current = facts.RoleActions.Count == 0 ? first : facts.RoleActions[^1].Action;
        if (current.RoleSid != execution.RoleSid.Value)
        {
            await new MirrorPulseNamespaceReplacementRoleRecovery(catalog, router, execution, actor)
                .RecoverAsync(operationId, file, parent, token).ConfigureAwait(false);
            facts = (await catalog.ReadNamespaceReplacementExecutionAsync(operationId, token).ConfigureAwait(false))!;
            current = MirrorPulseNamespaceReplacementContentWriter.RequireCurrentAction(facts);
        }
        var writer = new MirrorPulseNamespaceReplacementContentWriter(catalog, backups, router, execution, actor);
        if (facts.ContentCompletion is null)
        {
            var tightened = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router, current.LocalObject, identity,
                async (lease, stop) =>
                {
                    var observed = await lease.InspectAsync(stop).ConfigureAwait(false);
                    if (observed.LocalObject != current.LocalObject || observed.OwnerSid != current.OwnerSid || observed.IsDirectory || observed.LinkCount != 1 ||
                        !(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.RestoreDacl, observed.Dacl) ||
                          MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.RoleOnlyDacl, observed.Dacl)))
                        throw new InvalidDataException("Tightening requires the original single-link object and its owned descriptor.");
                    await lease.ApplyDaclAsync(current.RoleOnlyDacl, stop).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            if (tightened.Outcome != CloudProtectedLocalOperationOutcome.Completed || !tightened.CallbackCompleted || !tightened.Drained ||
                !tightened.AccessDescriptorApplied || !tightened.AccessDescriptorReadBack || !tightened.DurableProjectionCommitted)
                throw new IOException("The public replacement protection scope requires recovery.");
        }
        await writer.CopyAsync(operationId, file, parent, input, token).ConfigureAwait(false);
        var restored = await new MirrorPulseNamespaceReplacementAccessRestorer(catalog, router, execution, actor)
            .RestoreForCompletionAsync(operationId, file, parent, token).ConfigureAwait(false);
        if (!restored.ProductVerified || !restored.Drained)
            throw new IOException("The replacement access restoration requires recovery.");
        return await CompleteAdmittedAsync(operationId, file, parent, token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceReplacementCompletion> CompleteAdmittedAsync(Guid operationId, CloudFile file,
        CloudDirectory parent, CancellationToken token)
    {
        var facts = (await catalog.ReadNamespaceReplacementExecutionAsync(operationId, token).ConfigureAwait(false))!;
        var parentProof = await MirrorPulseNamespaceReplacementContentWriter.ReadCurrentParentAsync(catalog, facts.Intent, token).ConfigureAwait(false);
        await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parent, router, token).ConfigureAwait(false);
        await using var objectGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, router, token).ConfigureAwait(false);
        var before = await file.InspectAsync(token).ConfigureAwait(false);
        if (!before.IsPlaceholder || before.ContentAvailability != CloudContentAvailability.FullyAvailable)
            throw new InvalidDataException("Completion requires resident original content without source recall.");
        await using var dataGuard = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var child = await objectGuard.InspectAsync(token).ConfigureAwait(false);
        var parentNow = await parentGuard.InspectAsync(token).ConfigureAwait(false);
        var snapshot = await file.InspectAsync(token).ConfigureAwait(false);
        return await catalog.CompleteNamespaceReplacementAsync(new(1, operationId, facts.Intent.MemberId,
            facts.Restoration!.ActionId, facts.Restoration.RestorationId, Guid.NewGuid(), Guid.Empty, parentProof,
            new(child.LocalObject, child.OwnerSid, child.Dacl, child.ObservedAt),
            new(parentNow.LocalObject, parentNow.OwnerSid, parentNow.Dacl, parentNow.ObservedAt),
            child.LinkCount ?? throw new InvalidDataException("Completion has no original link-count observation."), snapshot.IsPlaceholder,
            snapshot.SynchronizationState == CloudSynchronizationState.InSync, Convert.ToBase64String(snapshot.PlaceholderIdentity.Span), DateTimeOffset.UtcNow), token).ConfigureAwait(false);
    }
}
