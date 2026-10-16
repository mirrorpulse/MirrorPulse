using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Resumes an authorized deletion without returning local content to the upload pipeline.</summary>
public sealed class MirrorPulseConflictDeletionExecutor(MirrorPulseProductCatalog catalog,
    IMirrorPulseWorkerMutationTransport mutations, MirrorPulseMutationReadback readback)
{
    /// <remarks>
    /// Local completion must preserve the authorized original bytes and retire that same
    /// native object. Acknowledgement must settle every retained official journal ID in one
    /// transaction. These callbacks are Host boundaries, never client filesystem operations.
    /// </remarks>
    public async ValueTask<MirrorPulseConflictDeletion> ExecuteAsync(Guid commandId,
        Func<MirrorPulseConflictDeletion, CancellationToken, ValueTask> completeLocal,
        Func<MirrorPulseConflictDeletionIntent, CancellationToken, ValueTask> acknowledgeOriginals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completeLocal); ArgumentNullException.ThrowIfNull(acknowledgeOriginals);
        var decision = await catalog.ReadConflictDeletionAsync(commandId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The conflict deletion decision is missing.");
        if (decision.Phase == MirrorPulseConflictDeletionPhase.Completed) return decision;
        // Revalidate a retained archive and original local absence on every replay,
        // including after remote acceptance and before original acknowledgement.
        await completeLocal(decision, cancellationToken).ConfigureAwait(false);
        decision = await catalog.ReadConflictDeletionAsync(commandId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The local deletion decision disappeared.");
        if (decision.Phase < MirrorPulseConflictDeletionPhase.Retired)
            throw new InvalidOperationException("Local preservation and controlled retirement must complete before deleting remote content.");
        if (decision.Phase == MirrorPulseConflictDeletionPhase.Retired)
        {
            var intent = decision.Intent.RemoteDeletion;
            var record = await catalog.ReadMutationAsync(intent.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The authorized deletion lost its independent remote effect.");
            if (record.Intent != intent) throw new InvalidDataException("The authorized remote deletion changed.");
            var executor = new MirrorPulseMutationExecutor(catalog);
            if (record.State == MirrorPulseMutationState.Prepared && record.ExecutionEvidence == MirrorPulseMutationExecutionEvidence.NeverStarted)
                await executor.ExecutePreparedAsync(record, token => mutations.DeleteAsync(new(intent.InstanceId, intent.RelativePath,
                    intent.ExpectedRevision, false, intent.OperationId, intent.RootKey), token), RequireDeleted, cancellationToken).ConfigureAwait(false);
            else
                await executor.ReconcileAsync(record, readback.VerifyAsync, RequireDeleted, cancellationToken).ConfigureAwait(false);
            await catalog.AdvanceConflictDeletionAsync(commandId, MirrorPulseConflictDeletionPhase.Retired,
                MirrorPulseConflictDeletionPhase.RemoteAccepted, cancellationToken).ConfigureAwait(false);
            decision = decision with { Phase = MirrorPulseConflictDeletionPhase.RemoteAccepted };
        }
        await acknowledgeOriginals(decision.Intent, cancellationToken).ConfigureAwait(false);
        await catalog.AdvanceConflictDeletionAsync(commandId, MirrorPulseConflictDeletionPhase.RemoteAccepted,
            MirrorPulseConflictDeletionPhase.Completed, cancellationToken).ConfigureAwait(false);
        return decision with { Phase = MirrorPulseConflictDeletionPhase.Completed };

        static ValueTask RequireDeleted(string? revision, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (revision is not null) throw new InvalidDataException("A deletion cannot accept an extant remote revision.");
            return ValueTask.CompletedTask;
        }
    }
}
