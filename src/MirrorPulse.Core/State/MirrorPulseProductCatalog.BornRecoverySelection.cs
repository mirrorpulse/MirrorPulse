namespace MirrorPulse.Core.State;

/// <summary>Coherent planning inputs for the next original born recovery decision.</summary>
/// <remarks>
/// Detached facts from one fresh read transaction. They grant no native, current-role or
/// permission-write authority. Recovery execution must independently revalidate its action;
/// these facts cannot be reused across native work, mutations or a later selection.
/// </remarks>
public sealed record MirrorPulseBornMemberRecoverySelection(IReadOnlyList<MirrorPulseNamespaceMemberRecoveryContext> Lineage,
    MirrorPulseNamespaceMemberProtectionSnapshot Parent, MirrorPulseNamespaceBornMemberRecoveryAction? LatestAction,
    MirrorPulseNamespaceBornMemberRecoveryCompletion? LatestCompletion);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the complete actual lineage, direct parent and latest recovery history in one fresh snapshot.</summary>
    public async Task<MirrorPulseBornMemberRecoverySelection> ReadNamespaceBornMemberRecoverySelectionAsync(Guid initializationOperationId,
        Guid memberId, CancellationToken cancellationToken = default)
    {
        if (initializationOperationId == Guid.Empty || memberId == Guid.Empty)
            throw new ArgumentException("A pending initialization and member ID are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var lineage = await ReadNamespaceMemberRecoveryLineageCoreAsync(initializationOperationId, memberId, transaction, cancellationToken).ConfigureAwait(false);
            var history = await ReadBornMemberRecoveryHistoryCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
            var latest = history.Count == 0 ? null : history[^1];
            var parent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(lineage[0].Location.ParentMemberId
                ?? throw new InvalidDataException("Born recovery planning requires its registered direct parent."), transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Born recovery planning lost its settled direct parent.");
            MirrorPulseNamespaceBornMemberRecoveryCompletion? completion = null;
            if (latest is not null)
            {
                var action = await RequireBornMemberRecoveryActionCoreAsync(latest.ActionId, transaction, cancellationToken).ConfigureAwait(false);
                completion = await ReadBornMemberRecoveryCompletionCoreAsync(latest.ActionId, action, transaction, cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit(); return new(lineage, parent, latest, completion);
        }
        finally { _gate.Release(); }
    }
}
