namespace MirrorPulse.Core.State;

/// <summary>One currently validated born recovery action, initializer and full actual parent chain.</summary>
/// <remarks>
/// Facts belong to one fresh read transaction. They do not retain a lock, native authority or
/// permission-coordinator admission. Read again after acquiring that admission and compare the
/// original action; never reuse these facts across native operations or catalog writes.
/// </remarks>
public sealed record MirrorPulseBornMemberRecoveryAdmission(MirrorPulseNamespaceBornMemberRecoveryAction Action,
    MirrorPulseNamespaceInitializationStatus Initialization, IReadOnlyList<MirrorPulseNamespaceMemberRecoveryContext> Lineage);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Returns the complete facts already validated for one latest unfinished born recovery action.</summary>
    public async Task<MirrorPulseBornMemberRecoveryAdmission> ReadNamespaceBornMemberRecoveryAdmissionAsync(Guid actionId,
        CancellationToken cancellationToken = default)
    {
        if (actionId == Guid.Empty) throw new ArgumentException("An action ID is required.", nameof(actionId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var admission = await ReadBornMemberRecoveryAdmissionCoreAsync(actionId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return admission;
        }
        finally { _gate.Release(); }
    }
}
