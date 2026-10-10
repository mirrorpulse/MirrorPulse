using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Persists remote execution intent before dispatch; uncertainty never authorizes another mutation.</summary>
public sealed class MirrorPulseMutationExecutor(MirrorPulseProductCatalog catalog)
{
    private readonly MirrorPulseProductCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public async ValueTask ReconcileAsync(MirrorPulseMutationRecord record,
        Func<MirrorPulseMutationRecord, CancellationToken, ValueTask<MirrorPulseMutationProof>> verify,
        Func<string?, CancellationToken, ValueTask> acknowledge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(acknowledge);
        if (record.State == MirrorPulseMutationState.Acknowledged) return;
        if (record.State == MirrorPulseMutationState.Superseded)
            throw new MirrorPulseMutationAmbiguousException("The original mutation is owned by its durable coalescing plan.");
        if (record.State == MirrorPulseMutationState.Conflict)
            throw new MirrorPulseWorkerMutationConflictException(record.Intent.ExpectedRevision, record.AcceptedRevision);
        if (record.State == MirrorPulseMutationState.Prepared) throw new MirrorPulseMutationAmbiguousException();
        if (record.State == MirrorPulseMutationState.RemoteAccepted &&
            await _catalog.ReadContentAcceptanceProofAsync(record.Intent.OperationId, cancellationToken).ConfigureAwait(false) is { } retained)
        {
            if (retained.UploadBinding != record.Intent.UploadBinding || retained.Length != record.Intent.ContentLength ||
                !string.Equals(retained.Sha256, record.Intent.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
                retained.AcceptedRevision != record.AcceptedRevision)
                throw new InvalidDataException("The retained proof does not match its accepted mutation.");
            // Remote acceptance is already durable. Reverify the same local proof rather than
            // replace its revision/identity with a later remote observation or upload again.
            await acknowledge(retained.AcceptedRevision, cancellationToken).ConfigureAwait(false);
            await _catalog.TransitionMutationAsync(record.Intent.OperationId, MirrorPulseMutationState.RemoteAccepted,
                MirrorPulseMutationState.Acknowledged, retained.AcceptedRevision, cancellationToken).ConfigureAwait(false);
            return;
        }
        MirrorPulseMutationProof proof = await verify(record, cancellationToken).ConfigureAwait(false);
        if (proof.Kind == MirrorPulseMutationProofKind.Unknown) throw new MirrorPulseMutationAmbiguousException();
        if (proof.Kind == MirrorPulseMutationProofKind.Conflict)
        {
            await _catalog.TransitionMutationAsync(record.Intent.OperationId, record.State,
                MirrorPulseMutationState.Conflict, proof.Revision, cancellationToken).ConfigureAwait(false);
            throw new MirrorPulseWorkerMutationConflictException(record.Intent.ExpectedRevision, proof.Revision);
        }
        await _catalog.TransitionMutationAsync(record.Intent.OperationId, record.State,
            MirrorPulseMutationState.RemoteAccepted, proof.Revision, cancellationToken).ConfigureAwait(false);
        await acknowledge(proof.Revision, cancellationToken).ConfigureAwait(false);
        await _catalog.TransitionMutationAsync(record.Intent.OperationId, MirrorPulseMutationState.RemoteAccepted,
            MirrorPulseMutationState.Acknowledged, proof.Revision, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ExecuteAsync(MirrorPulseMutationIntent intent,
        Func<CancellationToken, ValueTask<string?>> mutate,
        Func<string?, CancellationToken, ValueTask> acknowledge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(acknowledge);
        MirrorPulseMutationRecord record = await _catalog.PrepareMutationAsync(intent, cancellationToken).ConfigureAwait(false);
        await ExecutePreparedAsync(record, mutate, acknowledge, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ExecutePreparedAsync(MirrorPulseMutationRecord record,
        Func<CancellationToken, ValueTask<string?>> mutate,
        Func<string?, CancellationToken, ValueTask> acknowledge, CancellationToken cancellationToken)
    {
        MirrorPulseMutationIntent intent = record.Intent;
        if (record.State == MirrorPulseMutationState.Acknowledged) return;
        if (record.State != MirrorPulseMutationState.Prepared) throw new MirrorPulseMutationAmbiguousException();
        await _catalog.TransitionMutationAsync(intent.OperationId, record.State, MirrorPulseMutationState.Executing,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        string? revision;
        try { revision = await mutate(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing,
                exception is MirrorPulseWorkerMutationConflictException ? MirrorPulseMutationState.Conflict :
                    exception is NotSupportedException ? MirrorPulseMutationState.Prepared : MirrorPulseMutationState.Ambiguous,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            throw;
        }
        await _catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing,
            MirrorPulseMutationState.RemoteAccepted, revision, cancellationToken).ConfigureAwait(false);
        await acknowledge(revision, cancellationToken).ConfigureAwait(false);
        await _catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.RemoteAccepted,
            MirrorPulseMutationState.Acknowledged, revision, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MirrorPulseMutationAmbiguousException : IOException
{
    public MirrorPulseMutationAmbiguousException() : base("The previous remote outcome requires reconciliation before retry.") { }
    public MirrorPulseMutationAmbiguousException(string? message) : base(message) { }
    public MirrorPulseMutationAmbiguousException(string? message, Exception? innerException) : base(message, innerException) { }
}
