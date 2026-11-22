namespace MirrorPulse.Core.State;

/// <summary>A historical import snapshot, not fresh native authority or remote acceptance.</summary>
public sealed record MirrorPulseNamespaceImportStatus(Guid OperationId, string RelativePath, string Phase,
    long Length, string Sha256, bool Started, bool OriginalBindingRecorded, bool ContentRecorded,
    bool Converted, bool Protected, bool Registered, bool RequiresRecovery);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads retained import progress in one short catalog transaction without waiting for native copying.</summary>
    /// <remarks>This read owns no namespace actor, native handle or authority and never authorizes input reclamation.</remarks>
    public async Task<MirrorPulseNamespaceImportStatus> ReadNamespaceImportStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An import operation ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var birth = await ReadNamespaceBirthCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original import admission is missing.");
            var plan = await ReadNamespaceBirthPlanCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original import plan is missing.");
            if (birth.Origin != MirrorPulseNamespaceBirthOrigin.Import || birth.IsDirectory || plan.Content is null)
                throw new InvalidOperationException("Status requires an original file import plan.");
            bool started = await ReadNamespaceBirthStartCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is not null;
            bool binding = await ReadNamespaceBirthConversionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is not null;
            bool content = await ReadNamespaceBirthContentCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is not null;
            bool observed = await ReadNamespaceBirthObservationCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is not null;
            bool protection = await ReadLatestNamespaceBirthProtectionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is not null;
            bool registered = await ReadNamespaceMemberCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false) is not null;
            string phase = registered ? "Registered" : protection ? "Protected" : observed ? "Converted" : content ? "ContentRecorded" :
                binding ? "OriginalBindingRecorded" : started ? "RecoveryRequired" : "Prepared";
            var result = new MirrorPulseNamespaceImportStatus(operationId, birth.RelativePath, phase, plan.Content.Length, plan.Content.Sha256,
                started, binding, content, observed, protection, registered, started && !registered);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }
}
