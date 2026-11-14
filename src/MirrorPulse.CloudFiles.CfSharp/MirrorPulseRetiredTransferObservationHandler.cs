using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>The Host's original notification settlement, before ordinary journal ownership.</summary>
public interface IMirrorPulseRetiredTransferObservationHandler
{
    ValueTask RecoverPendingAsync(CancellationToken token);
    /// <summary>Another bounded recovery turn must precede an empty-feed wait.</summary>
    bool RecoveryScanInProgress => false;
    ValueTask<bool> TryAcknowledgeAsync(MirrorPulseLocalChangeObservation observation,
        MirrorPulseRetiredTransferSource source, CancellationToken token);
}

/// <summary>Accepts only a proved retired generation and invokes the actual public null-revision ACK.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseRetiredTransferObservationHandler(CloudFileSystem fileSystem, CloudLocalChangeFeed feed,
    MirrorPulseCfSharpStateSession state, MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor, MirrorPulseNamespaceWorkAdmission work,
    Func<InstanceId, bool> mayDispatch) : IMirrorPulseRetiredTransferObservationHandler
{
    private readonly MirrorPulseRetiredTransferRootCursor _recoveryRoots = new();

    public bool RecoveryScanInProgress => _recoveryRoots.RecoveryInProgress;

    public async ValueTask RecoverPendingAsync(CancellationToken token)
    {
        // Inspect at most four configured roots, including disabled/offline roots,
        // and replay at most two accepted original notifications per selected root.
        // Rotating the complete immutable snapshot reaches active roots even when
        // a long disabled prefix is retained. Replay revalidates current admission.
        var window = _recoveryRoots.Read(router, mayDispatch, token);
        foreach (var root in window.Roots)
        {
            var pending = await catalog.ReadPendingRetiredTransferObservationsAsync(root.RootId, limit: 2, token).ConfigureAwait(false);
            if (pending.Count != 0) _recoveryRoots.RetainPendingReplay();
            foreach (var start in pending) _ = await ReplayAcceptedAsync(start, token).ConfigureAwait(false);
            var directoryPending = await catalog.ReadPendingDirectoryTransferLateAcknowledgementsAsync(root.RootId, token).ConfigureAwait(false);
            if (directoryPending.Count != 0) _recoveryRoots.RetainPendingReplay();
            foreach (var start in directoryPending)
                if (work.TryEnter([root.RootId], out var admission))
                {
                    using (admission)
                        _ = await actor.RunAsync(async stop =>
                        {
                            await AcknowledgeDirectoryObservationAsync(start, stop).ConfigureAwait(false);
                            return true;
                        }, token).ConfigureAwait(false);
                }
        }
    }

    public async ValueTask<bool> TryAcknowledgeAsync(MirrorPulseLocalChangeObservation observation,
        MirrorPulseRetiredTransferSource source, CancellationToken token)
    {
        var retained = await catalog.ReadRetiredTransferObservationAcknowledgementAsync(observation.OperationId, token).ConfigureAwait(false);
        if (retained is not null) return await ReplayAcceptedAsync(retained, token).ConfigureAwait(false);
        if (source.Transfer.Acknowledgement?.AcknowledgedAt is null || source.Retirement.TransferSourceProjection is null ||
            source.Retirement.TransferSourceNameEpoch is null || !TryObservation(observation, out var original)) return false;
        var root = router.FindRegistration(source.Transfer.Plan.SourceRoute.RootId);
        if (root is null || root.State != RootRegistrationState.Active || !mayDispatch(root.InstanceId) ||
            !work.TryEnter([root.RootId], out var admission)) return false;
        using var retainedAdmission = admission;
        return await actor.RunAsync(async stop =>
        {
            MirrorPulseRetiredTransferObservationAcknowledgement? start = null;
            await execution.RunNamespaceOperationAsync(async () =>
            {
                try { start = await AcceptNewUnderActorAsync(original!, source, stop).ConfigureAwait(false); }
                catch (InvalidOperationException) { } // Ambiguous ownership remains pending; never force another owner aside.
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }).ConfigureAwait(false);
            if (start is null) return false;
            await AcknowledgeAcceptedUnderActorAsync(start, stop).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    internal async ValueTask<bool> TryAcknowledgeDirectorySourceAsync(MirrorPulseLocalChangeObservation observation,
        MirrorPulseRetiredDirectoryTransferSource source, CancellationToken token)
    {
        var plan = source.Admission;
        var retired = source.Retirement;
        var member = plan.Members[retired.Ordinal].Source.Protection.Context;
        if (source.AcknowledgedAt is null || retired.RetiredAt is null ||
            observation.ItemId != retired.Projection.ItemId || observation.IsDirectory != member.Member.IsDirectory ||
            observation.RelativePath.Replace('\\', '/') != member.Location.RelativePath ||
            observation.PreviousRelativePath is not null ||
            observation.Kind is not (CloudLocalChangeKind.Create or CloudLocalChangeKind.ContentUpdate or CloudLocalChangeKind.MetadataUpdate or CloudLocalChangeKind.Delete))
            return false;
        var root = router.FindRegistration(plan.SourceRoute.RootId);
        if (root is null || root.State != RootRegistrationState.Active || root.InstanceId != plan.SourceRoute.InstanceId ||
            root.UniquenessKey != plan.SourceRoute.RootKey || !mayDispatch(root.InstanceId) ||
            !work.TryEnter([root.RootId], out var admission)) return false;
        using var retainedAdmission = admission;
        return await actor.RunAsync(async stop =>
        {
            var original = new MirrorPulseTransferJournalObservation(observation.OperationId, observation.Sequence,
                observation.Kind switch
                {
                    CloudLocalChangeKind.Create => MirrorPulseWorkerChangeKind.Create,
                    CloudLocalChangeKind.ContentUpdate => MirrorPulseWorkerChangeKind.ContentUpdate,
                    CloudLocalChangeKind.MetadataUpdate => MirrorPulseWorkerChangeKind.MetadataUpdate,
                    _ => MirrorPulseWorkerChangeKind.Delete
                }, observation.ItemId, observation.RelativePath, observation.PreviousRelativePath, observation.IsDirectory, observation.ObservedAt);
            var start = await catalog.ReadDirectoryTransferLateAcknowledgementAsync(observation.OperationId, stop).ConfigureAwait(false);
            if (start is not null && (start.TransferId != plan.Command.CommandId || start.Observation != original))
                return false;
            if (start is null && (await feed.BeginScanAsync(stop).ConfigureAwait(false)).RequiresFullRescan) return false;
            try
            {
                if (start is null) await execution.RunNamespaceOperationAsync(async () =>
                {
                    var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(plan.SourceParent.Context.Member.MemberId, stop)
                        .ConfigureAwait(false);
                    if (parent is null || parent.Context.Member.LocalObject != plan.SourceParent.Context.Member.LocalObject ||
                        parent.Context.Location.RelativePath != plan.SourceParent.Context.Location.RelativePath ||
                        parent.Context.Protection.RoleSid != execution.RoleSid.Value || parent.Context.Member.OwnerSid != execution.OwnerSid.Value)
                        return;
                    var parentItem = fileSystem.GetDirectory(parent.Context.Location.RelativePath);
                    await using var guard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(parentItem, router, stop).ConfigureAwait(false);
                    if (!Matches(await guard.InspectAsync(stop).ConfigureAwait(false), parent)) return;
                    var originalRoot = fileSystem.GetDirectory(plan.Members[0].Source.Protection.Context.Location.RelativePath);
                    var native = await originalRoot.InspectAsync(stop).ConfigureAwait(false);
                    if (native.Exists || !native.IsTombstone || native.ItemId != plan.Members[0].Source.Protection.Identity.ItemId)
                        return;
                    await using var read = await state.OpenStore.BeginTransactionAsync(stop).ConfigureAwait(false);
                    var row = await read.Items.GetByItemIdAsync(retired.Projection.ItemId, stop).ConfigureAwait(false);
                    try
                    {
                        MirrorPulseNamespaceTransferCoordinator.RequireOriginalSourceProjection(retired.Projection, row, tombstone: true,
                            member.Member.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File);
                    }
                    catch (InvalidDataException) { return; }
                    await read.RollbackAsync(stop).ConfigureAwait(false);
                    start = await catalog.StartDirectoryTransferLateAcknowledgementAsync(plan.Command.CommandId, original,
                        DateTimeOffset.UtcNow, stop).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (Exception error) when (!stop.IsCancellationRequested &&
                error is IOException or InvalidOperationException or UnauthorizedAccessException)
            { return false; }
            if (start is null) return false;
            await AcknowledgeDirectoryObservationAsync(start, stop).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private async Task AcknowledgeDirectoryObservationAsync(MirrorPulseDirectoryTransferLateAcknowledgement start, CancellationToken token)
    {
        actor.AssertAccess();
        await execution.RunNormalUserOperationAsync(async () => await feed.AcknowledgeAsync(
            [new CloudLocalChangeAcknowledgement(start.Observation.OperationId, null)], token).ConfigureAwait(false)).ConfigureAwait(false);
        await catalog.CompleteDirectoryTransferLateAcknowledgementAsync(start, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<bool> ReplayAcceptedAsync(MirrorPulseRetiredTransferObservationAcknowledgement start, CancellationToken token)
    {
        var root = router.FindRegistration(start.SourceRootId);
        if (root is null || root.State != RootRegistrationState.Active || !mayDispatch(root.InstanceId) ||
            !work.TryEnter([root.RootId], out var admission)) return false;
        using var retainedAdmission = admission;
        return await actor.RunAsync(async stop =>
        {
            var retained = await catalog.ReadRetiredTransferObservationAcknowledgementAsync(start.Observation.OperationId, stop).ConfigureAwait(false)
                ?? throw new InvalidDataException("An accepted original notification lost its durable first start.");
            if ((retained with { AcknowledgedAt = null }) != (start with { AcknowledgedAt = null }))
                throw new InvalidDataException("The pending original notification changed before its public ACK replay.");
            // Its meaning was durably accepted before the original invocation. Later target
            // edits, source replacements or missing SDK rows cannot change this owned ID.
            await AcknowledgeAcceptedUnderActorAsync(retained, stop).ConfigureAwait(false); return true;
        }, token).ConfigureAwait(false);
    }

    private async Task AcknowledgeAcceptedUnderActorAsync(MirrorPulseRetiredTransferObservationAcknowledgement start, CancellationToken token)
    {
        actor.AssertAccess();
        await execution.RunNormalUserOperationAsync(async () =>
            await feed.AcknowledgeAsync([new CloudLocalChangeAcknowledgement(start.Observation.OperationId, null)], token).ConfigureAwait(false)).ConfigureAwait(false);
        // Only the actual API return authorizes settlement. An absent row alone is never a receipt.
        _ = await catalog.CompleteRetiredTransferObservationAcknowledgementAsync(start with { AcknowledgedAt = null },
            DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<MirrorPulseRetiredTransferObservationAcknowledgement?> AcceptNewUnderActorAsync(
        MirrorPulseTransferJournalObservation observation, MirrorPulseRetiredTransferSource source, CancellationToken token)
    {
        actor.AssertAccess(); var copy = source.Transfer; var original = source.Retirement.TransferSourceProjection!;
        var parent = await catalog.ReadNamespaceMemberProtectionSnapshotAsync(copy.Plan.SourceParent.Context.Member.MemberId, token).ConfigureAwait(false);
        if (parent is null || parent.Context.Member.LocalObject != copy.Plan.SourceParent.Context.Member.LocalObject ||
            parent.Context.Member.RootId != copy.Plan.SourceRoute.RootId || parent.Context.Protection.RoleSid != execution.RoleSid.Value ||
            parent.Context.Member.OwnerSid != execution.OwnerSid.Value) return null;
        await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(
            fileSystem.GetDirectory(parent.Context.Location.RelativePath), router, token).ConfigureAwait(false);
        if (!Matches(await parentGuard.InspectAsync(token).ConfigureAwait(false), parent)) return null;
        string name = copy.Plan.Source.Context.Location.RelativePath[(copy.Plan.Source.Context.Location.RelativePath.LastIndexOf('/') + 1)..];
        string currentPath = parent.Context.Location.RelativePath + "/" + name;
        var replacementMember = await catalog.ReadNamespaceMemberChildAsync(parent.Context.Member.MemberId, name, token).ConfigureAwait(false);
        var replacement = replacementMember is null ? null : await catalog.ReadNamespaceMemberProtectionSnapshotAsync(replacementMember.MemberId, token).ConfigureAwait(false);
        CloudItem item = replacement?.Context.Member.IsDirectory == true ? fileSystem.GetDirectory(currentPath) : fileSystem.GetFile(currentPath);
        var native = await item.InspectAsync(token).ConfigureAwait(false);
        if (native.Exists != (replacement is not null)) return null;
        if (!native.Exists && (!native.IsTombstone || native.ItemId != original.ItemId)) return null;
        await using var childGuard = native.Exists ? await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(item, router, token).ConfigureAwait(false) : null;
        if (native.Exists && (native.IsTombstone || replacement!.Context.Member.MemberId == copy.Plan.Source.Context.Member.MemberId ||
            replacement.Context.Member.LocalObject == original.LocalObject || replacement.Identity.ItemId is null ||
            replacement.Identity.ItemId == original.ItemId || native.ItemId != replacement.Identity.ItemId ||
            !Matches(await childGuard!.InspectAsync(token).ConfigureAwait(false), replacement))) return null;
        await using (var read = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            var row = await read.Items.GetByItemIdAsync(original.ItemId, token).ConfigureAwait(false);
            if (row is null || row.ItemId != original.ItemId || !row.IsTombstone || row.Kind != CloudItemKind.File || row.RemoteId != original.RemoteId ||
                row.RemoteRevision != original.RemoteRevision || row.LocalFileId != original.OfficialLocalFileId ||
                row.RelativePath.Replace('\\', '/') != currentPath) return null;
            await read.RollbackAsync(token).ConfigureAwait(false);
        }
        if ((await feed.BeginScanAsync(token).ConfigureAwait(false)).RequiresFullRescan) return null;
        // The same parent/current replacement guards remain held until the semantic start is
        // durable. The catalog checks its name epoch, so intervening missing generations cannot
        // be guessed from the current path or the SDK's nullable Int64 file identifier.
        return await catalog.StartRetiredTransferObservationAcknowledgementAsync(copy.Plan.OperationId, observation,
            DateTimeOffset.UtcNow, replacement?.Context.Member.MemberId, token).ConfigureAwait(false);
    }

    private static bool Matches(MirrorPulseNamespacePermissionObject native, MirrorPulseNamespaceMemberProtectionSnapshot member) =>
        native.LocalObject == member.Context.Member.LocalObject && native.RootId == member.Context.Member.RootId &&
        native.RelativePath == member.Context.Location.RelativePath && native.IsDirectory == member.Context.Member.IsDirectory &&
        native.LinkCount == 1 && native.OwnerSid == member.Context.Member.OwnerSid &&
        MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(member.ExpectedDacl, native.Dacl);

    private static bool TryObservation(MirrorPulseLocalChangeObservation value, out MirrorPulseTransferJournalObservation? result)
    {
        MirrorPulseWorkerChangeKind? kind = value.Kind switch
        {
            CloudLocalChangeKind.Create => MirrorPulseWorkerChangeKind.Create,
            CloudLocalChangeKind.ContentUpdate => MirrorPulseWorkerChangeKind.ContentUpdate,
            CloudLocalChangeKind.MetadataUpdate => MirrorPulseWorkerChangeKind.MetadataUpdate,
            CloudLocalChangeKind.Delete => MirrorPulseWorkerChangeKind.Delete,
            _ => null
        };
        result = kind is null ? null : new(value.OperationId, value.Sequence, kind.Value, value.ItemId,
            value.RelativePath, value.PreviousRelativePath, value.IsDirectory, value.ObservedAt);
        return result is not null && !value.IsDirectory && value.ItemId is not null && value.PreviousRelativePath is null;
    }
}
