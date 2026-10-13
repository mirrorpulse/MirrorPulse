using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Executes owned remote effects using the normal durable mutation and Worker boundaries.</summary>
/// <remarks>Effect completion does not confirm local content, project identity or acknowledge original journal IDs.</remarks>
public sealed class MirrorPulseJournalCoalescingExecutor(MirrorPulseProductCatalog catalog,
    IMirrorPulseWorkerStatTransport stats, IMirrorPulseWorkerUploadTransport uploads,
    IMirrorPulseWorkerMutationTransport mutations, MirrorPulseMutationReadback readback)
{
    private readonly MirrorPulseMutationExecutor _executor = new(catalog);

    /// <param name="openContent">Opens the same retained native object and final content proof; it must reject a replacement.</param>
    public ValueTask<IReadOnlyList<MirrorPulseMutationRecord>> ExecuteAsync(Guid planId,
        Func<MirrorPulseCoalescingContent, CancellationToken, ValueTask<Stream>> openContent,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(planId, openContent, (_, _) => ValueTask.CompletedTask, cancellationToken);

    /// <summary>Retains remote acceptance until the Host completes each effect's durable local projection.</summary>
    /// <remarks>
    /// Completion receives the actual accepted mutation before its effect ledger acknowledgement.
    /// Failure retains RemoteAccepted and prevents later effects. Reconciliation retries completion
    /// without redispatching an accepted remote write. Original official journal IDs remain separate;
    /// this callback and acknowledged effect history do not authorize acknowledging those IDs.
    /// </remarks>
    public async ValueTask<IReadOnlyList<MirrorPulseMutationRecord>> ExecuteAsync(Guid planId,
        Func<MirrorPulseCoalescingContent, CancellationToken, ValueTask<Stream>> openContent,
        Func<MirrorPulseMutationRecord, CancellationToken, ValueTask> completeEffect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openContent);
        ArgumentNullException.ThrowIfNull(completeEffect);
        MirrorPulseJournalCoalescingPlan plan = await catalog.ReadJournalCoalescingPlanAsync(planId, cancellationToken).ConfigureAwait(false);
        return await catalog.CoalescingScheduler.RunAsync(plan.InstanceId,
            token => ExecuteCoreAsync(planId, openContent, completeEffect, token), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<MirrorPulseMutationRecord>> ExecuteCoreAsync(Guid planId,
        Func<MirrorPulseCoalescingContent, CancellationToken, ValueTask<Stream>> openContent,
        Func<MirrorPulseMutationRecord, CancellationToken, ValueTask> completeEffect,
        CancellationToken cancellationToken)
    {
        MirrorPulseJournalCoalescingExecution execution = await catalog.ReadJournalCoalescingExecutionAsync(planId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The immutable coalesced execution is missing.");
        var results = new List<MirrorPulseMutationRecord>(execution.Steps.Count);
        foreach (MirrorPulseCoalescingStep step in execution.Steps)
        {
            MirrorPulseMutationRecord record = await catalog.PrepareJournalCoalescingMutationAsync(planId, step.Ordinal, cancellationToken).ConfigureAwait(false);
            async ValueTask Complete(string? revision, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (step.Kind is MirrorPulseCoalescingStepKind.Upload or MirrorPulseCoalescingStepKind.Move && string.IsNullOrEmpty(revision))
                    throw new InvalidDataException("A completed remote effect requires an accepted revision.");
                var accepted = await catalog.ReadMutationAsync(step.OperationId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The accepted coalesced effect is missing.");
                if (accepted.State != MirrorPulseMutationState.RemoteAccepted || accepted.Intent != record.Intent || accepted.AcceptedRevision != revision)
                    throw new InvalidDataException("Local completion requires the unchanged accepted coalesced effect.");
                await completeEffect(accepted, token).ConfigureAwait(false);
            }
            if (record.State == MirrorPulseMutationState.Prepared)
            {
                if (record.ExecutionEvidence != MirrorPulseMutationExecutionEvidence.NeverStarted)
                    throw new MirrorPulseMutationAmbiguousException("Historical execution evidence cannot authorize another coalesced mutation.");
                await _executor.ExecutePreparedAsync(record, async token =>
                {
                    MirrorPulseMutationIntent intent = record.Intent;
                    switch (step.Kind)
                    {
                        case MirrorPulseCoalescingStepKind.VerifyAbsence:
                        case MirrorPulseCoalescingStepKind.VerifyUnchanged:
                            MirrorPulseMutationProof proof = await VerifyOnlyAsync(step, record, token).ConfigureAwait(false);
                            if (proof.Kind != MirrorPulseMutationProofKind.Verified)
                                throw new MirrorPulseWorkerMutationConflictException(intent.ExpectedRevision, proof.Revision);
                            return proof.Revision;
                        case MirrorPulseCoalescingStepKind.Delete:
                            return await mutations.DeleteAsync(new(intent.InstanceId, intent.RelativePath, intent.ExpectedRevision,
                                false, intent.OperationId, intent.RootKey), token).ConfigureAwait(false);
                        case MirrorPulseCoalescingStepKind.Move:
                            return await mutations.MoveAsync(new(intent.InstanceId, intent.PreviousRelativePath!, intent.RelativePath,
                                intent.ExpectedRevision, false, intent.OperationId, intent.RootKey, intent.RootKey), token).ConfigureAwait(false);
                        case MirrorPulseCoalescingStepKind.Upload:
                            await using (Stream content = await openContent(execution.Content!, token).ConfigureAwait(false))
                                return await uploads.UploadAsync(new(intent.InstanceId, intent.RelativePath, intent.ExpectedRevision,
                                    content, intent.ContentLength!.Value, intent.OperationId, intent.ContentSha256, intent.RootKey), token).ConfigureAwait(false);
                        default: throw new InvalidDataException("The coalesced effect is unsupported.");
                    }
                }, Complete, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _executor.ReconcileAsync(record, (retained, token) => step.Kind is MirrorPulseCoalescingStepKind.VerifyAbsence or MirrorPulseCoalescingStepKind.VerifyUnchanged
                    ? VerifyOnlyAsync(step, retained, token) : readback.VerifyAsync(retained, token), Complete, cancellationToken).ConfigureAwait(false);
            }
            results.Add(await catalog.ReadMutationAsync(step.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The executed effect disappeared."));
        }
        return results.AsReadOnly();
    }

    private async ValueTask<MirrorPulseMutationProof> VerifyOnlyAsync(MirrorPulseCoalescingStep step,
        MirrorPulseMutationRecord record, CancellationToken token)
    {
        var intent = record.Intent;
        string? revision = await stats.StatAsync(new(intent.InstanceId, intent.RelativePath, intent.RootKey), token).ConfigureAwait(false);
        bool verified = step.Kind == MirrorPulseCoalescingStepKind.VerifyAbsence ? revision is null : revision == intent.ExpectedRevision;
        return new(verified ? MirrorPulseMutationProofKind.Verified : MirrorPulseMutationProofKind.Conflict, revision);
    }
}
