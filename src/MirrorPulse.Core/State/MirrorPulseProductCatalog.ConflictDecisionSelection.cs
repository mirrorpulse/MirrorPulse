using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private sealed record ConflictDecisionSelection(InstanceId InstanceId, Guid? PlanId, string? PlanFingerprint,
        string RemoteTarget, string? RemoteRevision, MirrorPulseMutationIntent? RejectedEffect,
        IReadOnlyList<Guid> Originals, IReadOnlyList<Guid> Conflicts);

    private async Task<ConflictDecisionSelection> ReadConflictDecisionSelectionAsync(Guid conflictId, string rootKey,
        Guid itemId, string localPath, MirrorPulseWorkerChangeCommand? observation, bool deletesRemote,
        SqliteTransaction transaction, CancellationToken token)
    {
        var selected = await ReadDeletionConflictAsync(conflictId, transaction, token).ConfigureAwait(false);
        if (observation is not null) ValidateDeletionObservation(observation, selected.OriginalId, selected.InstanceId, rootKey, itemId);
        Guid? planId = null;
        await using (var owner = _connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = "SELECT plan_id FROM journal_coalescing_members WHERE operation_id=$operation;";
            owner.Parameters.AddWithValue("$operation", selected.OriginalId.ToString("D"));
            if (await owner.ExecuteScalarAsync(token).ConfigureAwait(false) is string value) planId = Guid.Parse(value);
        }
        var originals = new List<Guid>(); var conflicts = new List<Guid>();
        string? fingerprint = null; string remoteTarget;
        MirrorPulseMutationIntent? rejected = null;
        if (planId is { } ownerId)
        {
            if (observation is not null) throw new ArgumentException("A single observation cannot replace an owned group's complete execution.", nameof(observation));
            var plan = await ReadCoalescingPlanCoreAsync(ownerId, transaction, token).ConfigureAwait(false);
            var original = await ReadCoalescingExecutionCoreAsync(ownerId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The rejected group lost its original execution.");
            var execution = await ReadCoalescingDispatchCoreAsync(plan, original, transaction, token).ConfigureAwait(false);
            if (plan.ItemId != itemId || plan.InstanceId != selected.InstanceId || plan.RootKey != rootKey)
                throw new InvalidDataException("A decision cannot adopt another group's logical item or root.");
            fingerprint = original.PlanFingerprint;
            string? baseline = execution.ExpectedRevision;
            foreach (var step in execution.Steps)
            {
                var intent = await ReadCurrentCoalescingIntentAsync(plan, execution, step.Ordinal, baseline, transaction, token).ConfigureAwait(false);
                var record = await ReadMutationCoreAsync(intent.OperationId, token, transaction).ConfigureAwait(false);
                if (record is { State: MirrorPulseMutationState.Acknowledged })
                {
                    if (step.Kind == MirrorPulseCoalescingStepKind.Move || execution.HasMoveContent && step.Ordinal == 0)
                        baseline = record.AcceptedRevision;
                    continue;
                }
                if (record is not { State: MirrorPulseMutationState.Conflict } || !SameIntent(record.Intent, intent))
                    throw new InvalidOperationException("Unknown outcomes require reconciliation before an explicit local decision.");
                if (deletesRemote && execution.Steps.Skip(step.Ordinal + 1).Any(later => later.Kind != MirrorPulseCoalescingStepKind.Upload))
                    throw new NotSupportedException("Remaining remote verification requires its own explicit decision.");
                rejected = intent; break;
            }
            if (rejected is null) throw new InvalidOperationException("The group has no current rejected effect.");
            remoteTarget = rejected.Kind == MirrorPulseWorkerChangeKind.Move ? rejected.PreviousRelativePath! : rejected.RelativePath;
            foreach (var member in plan.Members)
            {
                await using var query = _connection.CreateCommand(); query.Transaction = transaction;
                query.CommandText = "SELECT conflict_id FROM upload_conflicts WHERE change_id=$original AND status=0;";
                query.Parameters.AddWithValue("$original", member.OperationId.ToString("D"));
                await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    throw new InvalidOperationException("Every original group member requires pending conflict evidence.");
                Guid id = Guid.Parse(reader.GetString(0));
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    throw new InvalidDataException("A group member has multiple pending conflict epochs.");
                originals.Add(member.OperationId); conflicts.Add(id);
            }
        }
        else
        {
            var record = await ReadMutationCoreAsync(selected.OriginalId, token, transaction).ConfigureAwait(false);
            if (record is null)
            {
                if (observation is null) throw new InvalidOperationException("A preflight conflict requires its exact public journal observation.");
                remoteTarget = observation.Kind == MirrorPulseWorkerChangeKind.Move ? observation.PreviousRelativePath! : observation.RelativePath;
            }
            else
            {
                if (record is not { State: MirrorPulseMutationState.Conflict, Intent.Origin: MirrorPulseMutationOrigin.Journal } ||
                    record.Intent.InstanceId != selected.InstanceId || record.Intent.RootKey != rootKey || record.Intent.IsDirectory ||
                    observation is not null && (record.Intent.Kind != observation.Kind || record.Intent.RelativePath != observation.RelativePath ||
                        record.Intent.PreviousRelativePath != observation.PreviousRelativePath))
                    throw new InvalidOperationException("An original mutation must remain explicitly rejected and match its public observation.");
                rejected = record.Intent;
                remoteTarget = record.Intent.Kind == MirrorPulseWorkerChangeKind.Move ? record.Intent.PreviousRelativePath! : record.Intent.RelativePath;
            }
            originals.Add(selected.OriginalId); conflicts.Add(conflictId);
        }
        if (!conflicts.Contains(conflictId)) throw new InvalidDataException("The selected conflict is not the current group epoch.");
        foreach (Guid id in conflicts)
        {
            var evidence = await ReadDeletionConflictAsync(id, transaction, token).ConfigureAwait(false);
            if (evidence.InstanceId != selected.InstanceId || evidence.Revision != selected.Revision || evidence.LocalPath != localPath)
                throw new InvalidDataException("The complete group's conflict provenance or observed remote revisions disagree.");
        }
        await RequireCurrentObservedConflictEpochsAsync(conflicts, planId, rejected?.OperationId, transaction, token).ConfigureAwait(false);
        return new(selected.InstanceId, planId, fingerprint, remoteTarget, selected.Revision, rejected,
            originals.AsReadOnly(), conflicts.AsReadOnly());
    }
}
