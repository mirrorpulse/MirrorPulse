using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    // A Move does not accept uploaded bytes. This narrowly validates that its
    // original program retained a real accepted upload for the unchanged bytes.
    private async Task RequireObservedMoveContentProofAsync(MirrorPulseMutationRecord move, MirrorPulseContentAcceptanceProof proof,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (move.Intent.Kind != MirrorPulseWorkerChangeKind.Move || move.Intent.IsDirectory ||
            move.Intent.Origin != MirrorPulseMutationOrigin.CoalescedJournal || move.Intent.UploadBinding is null ||
            move.State is not (MirrorPulseMutationState.RemoteAccepted or MirrorPulseMutationState.Acknowledged))
            throw new InvalidDataException("Move content confirmation requires its original coalesced accepted move.");
        Guid planId;
        await using (var owner = _connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = "SELECT plan_id FROM journal_coalescing_steps WHERE operation_id=$id UNION ALL SELECT plan_id FROM journal_coalescing_retries WHERE operation_id=$id;";
            owner.Parameters.AddWithValue("$id", move.Intent.OperationId.ToString("D"));
            if (await owner.ExecuteScalarAsync(token).ConfigureAwait(false) is not string value || !Guid.TryParseExact(value, "D", out planId))
                throw new InvalidDataException("The accepted move lost its original program ownership.");
        }
        var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, token).ConfigureAwait(false);
        var original = await ReadCoalescingExecutionCoreAsync(planId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The observed move execution is missing.");
        if (original is not { Version: 3, IsMoveOnly: true, ObservedContent: { } observed } ||
            move.Intent.ExpectedRevision != original.ExpectedRevision || proof.OperationId != move.Intent.OperationId ||
            proof.AcceptedRevision != move.AcceptedRevision || proof.AcceptedItemId != observed.ItemId || proof.RemoteId != observed.RemoteId ||
            proof.UploadBinding != observed.Content.UploadBinding || proof.Length != observed.Content.Length ||
            !string.Equals(proof.Sha256, observed.Content.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The move cannot adopt new bytes, another identity or an unproved acceptance.");
        var dispatch = await ReadCoalescingDispatchCoreAsync(plan, original, transaction, token).ConfigureAwait(false);
        if (dispatch.HasMoveContent || !SameIntent(move.Intent,
            await ReadCurrentCoalescingIntentAsync(plan, dispatch, 0, original.ExpectedRevision, transaction, token).ConfigureAwait(false)))
            throw new InvalidDataException("An explicit move-and-upload decision must confirm only its later accepted upload.");
    }
}
