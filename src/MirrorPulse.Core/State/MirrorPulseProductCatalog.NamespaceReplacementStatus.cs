namespace MirrorPulse.Core.State;

/// <summary>Historical replacement progress; it grants no native authority, remote acceptance or tree readiness.</summary>
public sealed record MirrorPulseNamespaceReplacementStatus(Guid OperationId, Guid MemberId, string RelativePath, string Phase,
    long Length, string Sha256, bool Started, bool ActionRecorded, bool BackupRecorded, bool WriteStarted,
    bool ContentRecorded, bool ProtectionRestored, bool Completed, bool Cancelled, bool RequiresRecovery);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceReplacementStatus> ReadNamespaceReplacementStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        RequireNamespaceReplacementId(operationId); await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var facts = await ReadNamespaceReplacementExecutionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original replacement admission is missing.");
            bool completed = await ReadReplacementCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is not null;
            bool restored = facts.RestorationResult is { ProductVerified: true, Drained: true };
            bool cancelled = facts.Cancellation is not null;
            string phase = completed ? "Completed" : cancelled ? "Cancelled" : restored ? "ProtectionRestored" : facts.ContentCompletion is not null ? "ContentRecorded" :
                facts.WriteStart is not null ? "WriteStarted" : facts.Backup is not null ? "BackupRecorded" : facts.Action is not null ? "Prepared" : "AdmissionRecorded";
            var result = new MirrorPulseNamespaceReplacementStatus(operationId, facts.Intent.MemberId, facts.Intent.Original.Context.Location.RelativePath, phase,
                facts.Intent.Content.Length, facts.Intent.Content.Sha256, facts.Start is not null, facts.Action is not null, facts.Backup is not null,
                facts.WriteStart is not null, facts.ContentCompletion is not null, restored, completed, cancelled, facts.Start is not null && !completed && !cancelled);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }
}
