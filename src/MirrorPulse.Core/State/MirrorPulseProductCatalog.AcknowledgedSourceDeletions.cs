using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>An exact retired journal generation with a durably completed source deletion.</summary>
public sealed record MirrorPulseAcknowledgedSourceDeletion(Guid ItemId, InstanceId InstanceId, string RootKey,
    string RelativePath, bool IsDirectory, string ExpectedRevision);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads product-owned source results without interpreting or changing SDK journal history.</summary>
    /// <remarks>A tombstone, missing remote path or missing journal header alone is insufficient.</remarks>
    public async Task<IReadOnlyList<MirrorPulseAcknowledgedSourceDeletion>> ReadAcknowledgedSourceDeletionsAsync(
        IReadOnlySet<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        itemIds = itemIds.ToHashSet();
        if (itemIds.Contains(Guid.Empty)) throw new ArgumentException("A public object ID must be nonempty.", nameof(itemIds));
        if (itemIds.Count == 0) return [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var candidates = new List<(Guid OperationId, Guid? PlanId)>();
            // One history scan per full rescan, rather than one scan per tombstone.
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = """
                    SELECT operation_id,
                        (SELECT plan_id FROM journal_coalescing_steps WHERE operation_id=mutation_intents.operation_id
                         UNION ALL SELECT plan_id FROM journal_coalescing_retries WHERE operation_id=mutation_intents.operation_id OR followup_operation_id=mutation_intents.operation_id)
                    FROM mutation_intents WHERE state=$acknowledged AND json_extract(CAST(payload AS TEXT),'$.Kind')=$delete;
                    """;
                query.Parameters.AddWithValue("$acknowledged", (int)MirrorPulseMutationState.Acknowledged);
                query.Parameters.AddWithValue("$delete", (int)MirrorPulseWorkerChangeKind.Delete);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    candidates.Add((Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))));
            }
            var completed = new List<MirrorPulseAcknowledgedSourceDeletion>();
            foreach (var candidate in candidates)
            {
                MirrorPulseMutationRecord record = await ReadMutationCoreAsync(candidate.OperationId, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The completed source deletion disappeared.");
                if (record.State != MirrorPulseMutationState.Acknowledged)
                    throw new InvalidDataException("The completed source deletion lost its acknowledged state.");
                if (record.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.Started) continue;
                MirrorPulseMutationIntent intent = record.Intent;
                if (intent.Origin == MirrorPulseMutationOrigin.Journal && intent.JournalItemId is { } itemId && itemIds.Contains(itemId) &&
                    !string.IsNullOrEmpty(intent.ExpectedRevision))
                {
                    completed.Add(new(itemId, intent.InstanceId, intent.RootKey, intent.RelativePath, intent.IsDirectory, intent.ExpectedRevision));
                }
                else if (intent.Origin == MirrorPulseMutationOrigin.CoalescedJournal && candidate.PlanId is { } planId)
                {
                    var plan = await ReadCoalescingPlanCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false);
                    if (!itemIds.Contains(plan.ItemId)) continue;
                    var original = await ReadCoalescingExecutionCoreAsync(planId, transaction, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The completed source deletion lost its original execution.");
                    var dispatch = await ReadCoalescingDispatchCoreAsync(plan, original, transaction, cancellationToken).ConfigureAwait(false);
                    if (plan.Effect is not (MirrorPulseCoalescedEffect.DeleteFile or MirrorPulseCoalescedEffect.DeleteDirectory) ||
                        dispatch.Steps.Count != 1 || dispatch.Steps[0].Kind != MirrorPulseCoalescingStepKind.Delete ||
                        string.IsNullOrEmpty(original.ExpectedRevision)) continue;
                    var current = await ReadCurrentCoalescingIntentAsync(plan, dispatch, 0, dispatch.ExpectedRevision, transaction, cancellationToken).ConfigureAwait(false);
                    if (!SameIntent(intent, current)) continue; // An older accepted retry is not the current authorized effect.
                    completed.Add(new(plan.ItemId, plan.InstanceId, plan.RootKey, plan.FinalPath, plan.IsDirectory, original.ExpectedRevision));
                }
            }
            transaction.Commit(); return completed.AsReadOnly();
        }
        finally { _gate.Release(); }
    }
}
