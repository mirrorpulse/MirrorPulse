namespace MirrorPulse.Core.State;

/// <summary>Detached planning facts from one actual born-recovery read transaction.</summary>
/// <remarks>Historical completion carries no current lineage or parent authority. Pending
/// execution must still revalidate its immutable action before native work and completion.</remarks>
public sealed record MirrorPulseBornMemberRecoveryExecutionContext(
    IReadOnlyList<MirrorPulseNamespaceBornMemberRecoveryAction> History,
    MirrorPulseNamespaceBornMemberRecoveryAction? SelectedAction,
    MirrorPulseNamespaceBornMemberRecoveryCompletion? SelectedCompletion,
    IReadOnlyList<MirrorPulseNamespaceMemberRecoveryContext> Lineage,
    MirrorPulseNamespaceMemberProtectionSnapshot? Parent);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads all original action history and the selected completion or pending current inputs coherently.</summary>
    public async Task<MirrorPulseBornMemberRecoveryExecutionContext> ReadNamespaceBornMemberRecoveryExecutionContextAsync(
        Guid initializationOperationId, Guid memberId, Guid actionId, CancellationToken cancellationToken = default)
    {
        if (initializationOperationId == Guid.Empty || memberId == Guid.Empty || actionId == Guid.Empty)
            throw new ArgumentException("The original initializer, member and selected action are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var history = await ReadBornMemberRecoveryHistoryCoreAsync(memberId, transaction, cancellationToken).ConfigureAwait(false);
            var selected = history.SingleOrDefault(action => action.ActionId == actionId);
            if (selected is not null && selected.InitializationOperationId != initializationOperationId)
                throw new InvalidOperationException("The selected born action belongs to another original initializer.");
            var completion = selected is null ? null
                : await ReadBornMemberRecoveryCompletionCoreAsync(actionId, selected, transaction, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<MirrorPulseNamespaceMemberRecoveryContext> lineage = [];
            MirrorPulseNamespaceMemberProtectionSnapshot? parent = null;
            if (completion is null)
            {
                lineage = await ReadNamespaceMemberRecoveryLineageCoreAsync(initializationOperationId, memberId, transaction, cancellationToken).ConfigureAwait(false);
                parent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(lineage[0].Location.ParentMemberId
                    ?? throw new InvalidDataException("Born recovery requires its registered direct parent."), transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The born parent requires coherent recovery first.");
            }
            transaction.Commit(); return new(history, selected, completion, lineage, parent);
        }
        finally { _gate.Release(); }
    }
}
