using System.Text.Json;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Finds the immutable owner of an original journal ID through the product index.</summary>
    public async Task<MirrorPulseJournalCoalescingPlan?> ReadJournalCoalescingOwnerAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original operation ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT plan_id FROM journal_coalescing_members WHERE operation_id=$operation;";
            query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            if (await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string value)
            { transaction.Commit(); return null; }
            if (!Guid.TryParseExact(value, "D", out Guid planId) || planId == Guid.Empty)
                throw new InvalidDataException("The coalescing owner index is invalid.");
            var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            if (plan.Members.Count(member => member.OperationId == operationId) != 1)
                throw new InvalidDataException("The original ID is absent from its indexed coalescing decision.");
            transaction.Commit(); return plan;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseJournalCoalescingPlan> ReadJournalCoalescingPlanAsync(Guid planId,
        CancellationToken cancellationToken = default)
    {
        if (planId == Guid.Empty) throw new ArgumentException("The coalescing plan ID is missing.", nameof(planId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return plan;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Prepares one owned effect from immutable execution proof, after every preceding effect has completed.</summary>
    /// <remarks>The effect ledger does not acknowledge any original official journal identity.</remarks>
    public async Task<MirrorPulseMutationRecord> PrepareJournalCoalescingMutationAsync(Guid planId, int ordinal,
        CancellationToken cancellationToken = default)
    {
        if (planId == Guid.Empty || ordinal is < 0 or > 1)
            throw new ArgumentException("The coalesced effect address is invalid.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            MirrorPulseJournalCoalescingExecution execution = await ReadCoalescingExecutionCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The immutable coalesced execution is missing.");
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, execution.Steps.Count);
            MirrorPulseJournalCoalescingPlan plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
            string? baseline = execution.ExpectedRevision;
            if (ordinal == 1)
            {
                MirrorPulseMutationIntent previousIntent = await ReadCurrentCoalescingIntentAsync(plan, execution, 0,
                    baseline, transaction, cancellationToken).ConfigureAwait(false);
                MirrorPulseMutationRecord? previous = await ReadMutationCoreAsync(previousIntent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
                if (previous is null || !SameIntent(previous.Intent, previousIntent) || previous.State != MirrorPulseMutationState.Acknowledged)
                    throw new MirrorPulseMutationAmbiguousException("The preceding coalesced effect has not completed with unchanged intent.");
                if (execution.Steps[0].Kind == MirrorPulseCoalescingStepKind.Move)
                    baseline = !string.IsNullOrEmpty(previous.AcceptedRevision) ? previous.AcceptedRevision :
                        throw new InvalidDataException("The accepted move revision is required for the subsequent upload.");
            }
            MirrorPulseMutationIntent intent = await ReadCurrentCoalescingIntentAsync(plan, execution, ordinal,
                baseline, transaction, cancellationToken).ConfigureAwait(false);
            MirrorPulseMutationRecord record = await PrepareMutationCoreAsync(intent,
                JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions), cancellationToken, transaction).ConfigureAwait(false);
            transaction.Commit();
            return record;
        }
        finally { _gate.Release(); }
    }

    private static MirrorPulseMutationIntent CoalescingIntent(MirrorPulseJournalCoalescingPlan plan,
        MirrorPulseJournalCoalescingExecution execution, int ordinal, string? baseline)
    {
        MirrorPulseCoalescingStep step = execution.Steps[ordinal];
        bool uploads = step.Kind == MirrorPulseCoalescingStepKind.Upload;
        MirrorPulseWorkerChangeKind kind = step.Kind switch
        {
            MirrorPulseCoalescingStepKind.Upload => plan.Effect == MirrorPulseCoalescedEffect.CreateFile
                ? MirrorPulseWorkerChangeKind.Create : MirrorPulseWorkerChangeKind.ContentUpdate,
            MirrorPulseCoalescingStepKind.Move => MirrorPulseWorkerChangeKind.Move,
            MirrorPulseCoalescingStepKind.Delete => MirrorPulseWorkerChangeKind.Delete,
            _ => MirrorPulseWorkerChangeKind.MetadataUpdate, // Verification only; no metadata mutation is sent.
        };
        return new(step.OperationId, plan.InstanceId, plan.RootKey, kind, step.RelativePath, step.PreviousRelativePath,
            false, baseline, uploads ? execution.Content!.Length : null, uploads ? execution.Content!.Sha256 : null,
            MirrorPulseMutationOrigin.CoalescedJournal, uploads ? execution.Content!.UploadBinding : null);
    }
}
