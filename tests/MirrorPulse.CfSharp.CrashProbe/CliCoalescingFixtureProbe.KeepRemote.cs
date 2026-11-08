using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CfSharp.CrashProbe;

public static partial class CliCoalescingFixtureProbe
{
    private sealed record KeepRemoteSettlement(Guid CommandId, Guid AcknowledgementId, string SelectedSha256, string PreservedLocalSha256);

    private static async Task<KeepRemoteSettlement> ReadKeepRemoteSettlementAsync(MirrorPulseStoragePaths paths,
        MirrorPulseProductCatalog catalog, ICloudStateStore store, MirrorPulseJournalCoalescingPlan plan,
        MirrorPulseMutationRecord rejected, Guid[] originals, MirrorPulseCoalescedConflictRemoteObservation? selectedObservation)
    {
        Guid commandId = await catalog.ReadConflictKeepRemoteOwnerAsync(originals[0])
            ?? throw new InvalidDataException("The original group has no KeepRemote owner.");
        foreach (Guid original in originals)
            if (await catalog.ReadConflictKeepRemoteOwnerAsync(original) != commandId)
                throw new InvalidDataException("The original journal IDs do not share the exact KeepRemote owner.");
        var decision = await catalog.ReadConflictKeepRemoteAsync(commandId)
            ?? throw new InvalidDataException("The original KeepRemote decision is missing.");
        var command = await catalog.ReadUserCommandAsync(commandId);
        if (command is not { Action: "keepremote", State: "resolved" } ||
            command.TargetId != decision.Intent.ConflictId.ToString("D") || decision.Intent.PlanId != plan.PlanId ||
            decision.Intent.RejectedEffect != rejected.Intent || !originals.SequenceEqual(decision.Intent.OriginalOperationIds) ||
            decision.LocalPreservation?.Copy is not { } local || decision.RemoteCopy is not { } remote)
            throw new InvalidDataException("The resolved KeepRemote command lost the original decision or private copies.");
        if (selectedObservation is not null && (!selectedObservation.SelectedConflictIds.Contains(decision.Intent.ConflictId) ||
            selectedObservation.RemoteRevision != decision.Intent.RemoteSource.RemoteRevision))
            throw new InvalidDataException("KeepRemote did not use the separately observed version.");
        var first = await catalog.ReadConflictKeepRemoteFileStartAsync(commandId)
            ?? throw new InvalidDataException("The settled command lost its original file start.");
        var acknowledgement = await catalog.ReadConflictKeepRemoteAcknowledgementAsync(commandId);
        var native = (await catalog.ReadConflictKeepRemoteNativeAttemptsAsync(commandId))
            .Single(attempt => attempt.Result?.Receipt.MayAcknowledge == true).Result!;
        if (acknowledgement?.Result is not { } acknowledged || !acknowledged.VerifiedMissingOriginals.SequenceEqual(originals) ||
            acknowledgement.Start.NativeAttemptId != native.AttemptId || native.Receipt.ObservedInSync != true ||
            !native.Receipt.DurableProjectionCommitted || native.Receipt.BytesVerified < remote.Length ||
            native.Child.LocalObject != first.Original.Context.Member.LocalObject ||
            (await catalog.ReadNamespaceDispatchFencedRootsAsync()).Count != 0)
            throw new InvalidDataException("The selected bytes, original native receipt, exact ACKs and owned fence are required.");

        var conflict = await catalog.ReadUploadConflictAsync(decision.Intent.ConflictId)
            ?? throw new InvalidDataException("The selected conflict disappeared.");
        var copies = new MirrorPulseConflictCopyStore(paths);
        await using var localCopy = await copies.OpenLocalDecisionAsync(commandId, conflict, local);
        await using var remoteCopy = await copies.OpenRemoteDecisionAsync(commandId, conflict, decision.Intent.RemoteSource);
        var localPath = Path.GetFullPath(first.Original.Context.Location.RelativePath, paths.SyncRootPath);
        await using var resident = File.OpenRead(localPath);
        if (Convert.ToHexString(await SHA256.HashDataAsync(localCopy.Data)) != local.Sha256 ||
            Convert.ToHexString(await SHA256.HashDataAsync(remoteCopy.Data)) != remote.Sha256 ||
            Convert.ToHexString(await SHA256.HashDataAsync(resident)) != remote.Sha256)
            throw new InvalidDataException("The resident selected file and both actual private copies differ from their original proofs.");
        await using var transaction = await store.BeginTransactionAsync();
        var projected = await transaction.Items.GetByItemIdAsync(plan.ItemId);
        if (projected is not { IsTombstone: false } || projected.RemoteId != first.Original.Identity.RemoteId ||
            projected.RemoteRevision != decision.Intent.RemoteSource.RemoteRevision ||
            !string.Equals(Path.GetFullPath(projected.RelativePath, paths.SyncRootPath), localPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The official store lost the selected current projection.");
        await transaction.RollbackAsync();
        return new(commandId, acknowledged.InvocationId, remote.Sha256, local.Sha256);
    }
}
