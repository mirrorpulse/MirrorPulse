namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Validates the original pending scopes before target readback. The Host still holds fresh native guards.</summary>
    public async Task<MirrorPulseNamespaceTransferCopy> ValidateNamespaceTransferTargetReadAsync(Guid operationId,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            if (copy.CopyAcceptedAt is null || copy.AcceptedRevision is null)
                throw new InvalidOperationException("A target readback cannot supply a missing original upload acceptance receipt.");
            await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    /// <remarks>The Host supplies actual complete revision-pinned readback and stable remote identity. This
    /// fact keeps both fences and cannot release source bytes or grant any native/public projection authority.</remarks>
    public async Task<MirrorPulseNamespaceTransferCopy> RecordNamespaceTransferTargetVerificationAsync(
        MirrorPulseTransferTargetVerification verification, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(verification.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            ValidateNamespaceTransferTargetVerification(copy, verification);
            if (copy.TargetVerification is { } retained)
            {
                if (retained != verification) throw new InvalidOperationException("The first verified target identity and content are immutable.");
                transaction.Commit(); return copy;
            }
            await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true).ConfigureAwait(false);
            copy = copy with { TargetVerification = verification };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateNamespaceTransferTargetVerification(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseTransferTargetVerification verification)
    {
        if (verification.Version != 1 || verification.OperationId != copy.Plan.OperationId || copy.CopyAcceptedAt is not { } accepted ||
            copy.AcceptedRevision is null || verification.AcceptedRevision != copy.AcceptedRevision ||
            verification.VerifiedAt < accepted || verification.Length != copy.Plan.Length || verification.Sha256 != copy.Plan.Sha256 ||
            InvalidRemoteUpdateText(verification.RemoteId, 4096))
            throw new ArgumentException("Target verification requires the original accepted copy and its whole content identity.", nameof(verification));
    }
}
