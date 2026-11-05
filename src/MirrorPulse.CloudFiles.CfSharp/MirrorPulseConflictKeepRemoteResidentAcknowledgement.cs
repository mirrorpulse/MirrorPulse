using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseKeepRemoteAcknowledgementCheckpoint { AfterStart, AfterSdkReturn }

/// <summary>Settles the immutable original claim using the running public journal feed.</summary>
/// <remarks>The caller holds the instance scheduler, namespace actor and admitted root through readback.</remarks>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictKeepRemoteResidentAcknowledgement(MirrorPulseProductCatalog catalog,
    MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed, MirrorPulseRootRouter router,
    MirrorPulseNamespaceExecutionSession execution, MirrorPulseNamespaceActor actor)
{
    internal Func<MirrorPulseKeepRemoteAcknowledgementCheckpoint, CancellationToken, ValueTask>? Checkpoint { get; init; }

    internal async Task<MirrorPulseConflictKeepRemoteAcknowledgementResult> AcknowledgeAsync(RootId admittedRoot,
        MirrorPulseConflictKeepRemoteCopies copies, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(copies);
        actor.AssertAccess();
        var intent = copies.Preparation.Intent;
        var first = await catalog.ReadConflictKeepRemoteFileStartAsync(intent.CommandId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("KeepRemote acknowledgement requires the original file start.");
        var root = router.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey);
        if (root.State != RootRegistrationState.Active || root.RootId != admittedRoot ||
            first.Original.Context.Member.RootId != admittedRoot || first.Parent.Context.Member.RootId != admittedRoot)
            throw new InvalidOperationException("KeepRemote acknowledgement requires its original admitted active root.");
        var retained = await catalog.ReadConflictKeepRemoteAcknowledgementAsync(intent.CommandId, token).ConfigureAwait(false);
        if (retained?.Result is { } historical) return historical;

        return await execution.RunNormalUserOperationAsync(async () =>
        {
            if (!feed.IsStarted) throw new InvalidOperationException("KeepRemote acknowledgement requires the running official journal.");
            using var store = await state.OpenStoreFileLeaseAsync(token).ConfigureAwait(false);
            var binding = new MirrorPulseCfSharpStoreFileBinding(first.StoreVolumeSerialNumber, first.StoreFileId);
            await store.RequireCurrentAsync(binding, token).ConfigureAwait(false);
            var claim = await catalog.ReadConflictKeepRemoteJournalAsync(intent.CommandId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("KeepRemote lost its original journal claim.");
            if (copies.Local is null || claim.LocalPreservation.Copy != copies.Local.Proof || claim.RemoteCopy != copies.Remote.Proof ||
                !claim.Originals.Select(row => row.OperationId).SequenceEqual(intent.OriginalOperationIds))
                throw new InvalidDataException("KeepRemote acknowledgement requires the original selected copies and exact claim.");

            var ids = claim.Originals.Select(row => row.OperationId).ToArray();
            int present = await CountPresentAsync(ids, token).ConfigureAwait(false);
            if (present == ids.Length)
            {
                var journal = new MirrorPulseConflictKeepRemoteJournal(state.OpenStore, catalog, router, feed);
                await journal.RevalidateRetainedOriginalsAsync(copies, token).ConfigureAwait(false);
            }
            else if (present != 0 || retained is null)
                throw new InvalidDataException("An unstarted or partially missing KeepRemote claim cannot be acknowledged.");

            var start = retained?.Start ?? await catalog.StartConflictKeepRemoteAcknowledgementAsync(intent.CommandId,
                binding.VolumeSerialNumber, binding.FileId, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            if (Checkpoint is not null)
                await Checkpoint(MirrorPulseKeepRemoteAcknowledgementCheckpoint.AfterStart, token).ConfigureAwait(false);
            await store.RequireCurrentAsync(binding, token).ConfigureAwait(false);
            // A lost return permits another real invocation of the same claim. Absence
            // alone cannot settle it. A null revision leaves later ordinary edits alone.
            await feed.AcknowledgeAsync(ids, token).ConfigureAwait(false);
            var returnedAt = DateTimeOffset.UtcNow;
            if (Checkpoint is not null)
                await Checkpoint(MirrorPulseKeepRemoteAcknowledgementCheckpoint.AfterSdkReturn, CancellationToken.None).ConfigureAwait(false);
            await store.RequireCurrentAsync(binding, CancellationToken.None).ConfigureAwait(false);
            if (await CountPresentAsync(ids, CancellationToken.None).ConfigureAwait(false) != 0)
                throw new InvalidDataException("The official KeepRemote acknowledgement left an original journal row pending.");
            await store.RequireCurrentAsync(binding, CancellationToken.None).ConfigureAwait(false);
            return await catalog.CompleteConflictKeepRemoteAcknowledgementAsync(
                new(intent.CommandId, start.InvocationId, ids, returnedAt, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<int> CountPresentAsync(IEnumerable<Guid> operationIds, CancellationToken token)
    {
        await using var transaction = await state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        int present = 0;
        foreach (Guid id in operationIds)
            if (await transaction.Operations.GetAsync(id, token).ConfigureAwait(false) is not null) present++;
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return present;
    }
}
