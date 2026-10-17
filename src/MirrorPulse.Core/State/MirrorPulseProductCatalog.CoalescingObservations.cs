using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Finds a completed accepted upload for this exact logical item, root, revision and native object.</summary>
    public async Task<MirrorPulseContentAcceptanceProof?> ReadAcknowledgedContentProofAsync(InstanceId instanceId, string rootKey,
        Guid itemId, string revision, MirrorPulseLocalFileBinding localObject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localObject);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            Guid? operationId;
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT proof.operation_id FROM content_acceptance_proofs proof
                    JOIN mutation_intents mutation USING(operation_id)
                    WHERE mutation.state=$acknowledged
                      AND json_extract(CAST(mutation.payload AS TEXT),'$.Kind') IN ($create,$update)
                      AND json_extract(CAST(mutation.payload AS TEXT),'$.InstanceId')=$instance
                      AND json_extract(CAST(mutation.payload AS TEXT),'$.RootKey')=$root
                      AND json_extract(CAST(proof.payload AS TEXT),'$.AcceptedItemId')=$item
                      AND json_extract(CAST(proof.payload AS TEXT),'$.AcceptedRevision')=$revision
                      AND json_extract(CAST(proof.payload AS TEXT),'$.UploadBinding.LocalObject')=$binding
                    ORDER BY mutation.updated_utc DESC,proof.operation_id DESC LIMIT 1;
                    """;
                query.Parameters.AddWithValue("$acknowledged", (int)MirrorPulseMutationState.Acknowledged);
                query.Parameters.AddWithValue("$create", (int)MirrorPulseWorkerChangeKind.Create);
                query.Parameters.AddWithValue("$update", (int)MirrorPulseWorkerChangeKind.ContentUpdate);
                query.Parameters.AddWithValue("$instance", instanceId.ToString()); query.Parameters.AddWithValue("$root", rootKey);
                query.Parameters.AddWithValue("$item", itemId.ToString("D")); query.Parameters.AddWithValue("$revision", revision);
                query.Parameters.AddWithValue("$binding", JsonSerializer.Serialize(localObject, TopologyJsonOptions));
                operationId = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string id ? Guid.Parse(id) : null;
            }
            if (operationId is not { } accepted) { transaction.Commit(); return null; }
            var proof = await ReadContentAcceptanceProofCoreAsync(accepted, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new InvalidDataException("The acknowledged content proof disappeared.");
            var receipt = await ReadContentConfirmationReceiptCoreAsync(accepted, transaction, cancellationToken).ConfigureAwait(false);
            if (receipt?.MayAcknowledge != true || string.IsNullOrEmpty(receipt.ObservedPlaceholderIdentity))
            { transaction.Commit(); return null; }
            await RequireAcknowledgedContentProofAsync(proof, instanceId, rootKey, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return proof;
        }
        finally { _gate.Release(); }
    }

    private async Task ValidateCoalescingObservedContentAsync(MirrorPulseJournalCoalescingPlan plan,
        MirrorPulseJournalCoalescingExecution execution, SqliteTransaction transaction, CancellationToken token)
    {
        if (execution.ObservedContent is not { } observed) return;
        if (execution.Version != (plan.Effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile ? 3 : 2) || execution.OriginalMutations.Count != 0)
            throw new InvalidDataException("An observed program cannot discard prepared original mutation intent.");
        var proof = await ReadContentAcceptanceProofCoreAsync(observed.AcceptanceOperationId, token, transaction).ConfigureAwait(false)
            ?? throw new InvalidDataException("The observed program has no actual historical acceptance proof.");
        await RequireAcknowledgedContentProofAsync(proof, plan.InstanceId, plan.RootKey, transaction, token).ConfigureAwait(false);
        var receipt = await ReadContentConfirmationReceiptCoreAsync(proof.OperationId, transaction, token).ConfigureAwait(false);
        if (proof.AcceptedItemId != plan.ItemId || proof.AcceptedItemId != observed.ItemId || proof.RemoteId != observed.RemoteId ||
            proof.AcceptedRevision != execution.ExpectedRevision || proof.AcceptedRevision != observed.RemoteRevision ||
            proof.UploadBinding.LocalObject != observed.Content.UploadBinding.LocalObject || proof.Length != observed.Content.Length ||
            string.IsNullOrEmpty(receipt?.ObservedPlaceholderIdentity) ||
            receipt.ObservedPlaceholderIdentity != observed.Content.UploadBinding.PreviousPlaceholderIdentity ||
            !string.Equals(proof.Sha256, observed.Content.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The current guarded bytes or native identity differ from the actual accepted content.");
    }

    private async Task RequireAcknowledgedContentProofAsync(MirrorPulseContentAcceptanceProof proof, InstanceId instanceId,
        string rootKey, SqliteTransaction transaction, CancellationToken token)
    {
        var record = await ReadMutationCoreAsync(proof.OperationId, token, transaction).ConfigureAwait(false);
        var receipt = await ReadContentConfirmationReceiptCoreAsync(proof.OperationId, transaction, token).ConfigureAwait(false);
        if (record is not { State: MirrorPulseMutationState.Acknowledged, Intent.IsDirectory: false } ||
            record.Intent.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate) ||
            record.Intent.InstanceId != instanceId || record.Intent.RootKey != rootKey || record.AcceptedRevision != proof.AcceptedRevision ||
            record.Intent.UploadBinding != proof.UploadBinding || record.Intent.ContentLength != proof.Length ||
            !string.Equals(record.Intent.ContentSha256, proof.Sha256, StringComparison.OrdinalIgnoreCase) || receipt?.MayAcknowledge != true)
            throw new InvalidDataException("Content observation requires the exact completed upload and successful native confirmation receipt.");
    }
}
