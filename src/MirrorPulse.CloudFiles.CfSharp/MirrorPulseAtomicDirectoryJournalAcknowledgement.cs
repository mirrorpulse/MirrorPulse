using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MirrorPulseAtomicDirectoryJournalAcknowledgement(MirrorPulseProductCatalog catalog,
    ICloudStateStore store, Func<IReadOnlyList<Guid>, CancellationToken, ValueTask> acknowledge)
{
    internal async ValueTask<IReadOnlyList<Guid>> AcknowledgeAsync(Guid admissionId, CancellationToken token)
    {
        var journal = await catalog.ReadAcceptedAtomicDirectoryJournalAsync(admissionId, token).ConfigureAwait(false);
        Guid[] ids = journal.Originals.Select(original => original.OperationId).ToArray();
        if (journal.Completed) return ids;
        int pending = await CountPendingOriginalsAsync(journal, token).ConfigureAwait(false);
        if (pending != 0 && pending != ids.Length)
            throw new InvalidDataException("The atomic directory batch has a partial official journal acknowledgement.");
        if (pending == 0 && journal.ReceiptFingerprint is null)
            throw new InvalidDataException("The official originals disappeared before the Host started acknowledgement.");

        journal = await catalog.BeginAtomicDirectoryJournalAcknowledgementAsync(admissionId, token).ConfigureAwait(false);
        if (journal.Completed) return ids;
        // CfSharp's public ID acknowledgement is idempotent. Recovery invokes
        // it again even when its first commit removed all the original rows.
        await acknowledge(ids, token).ConfigureAwait(false);
        await catalog.CompleteAtomicDirectoryJournalAcknowledgementAsync(admissionId, journal.ReceiptFingerprint!,
            CancellationToken.None).ConfigureAwait(false);
        return ids;
    }

    internal async ValueTask<int> CountPendingOriginalsAsync(MirrorPulseAtomicDirectoryJournal journal, CancellationToken token)
    {
        int pending = 0;
        await using var transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false);
        foreach (var original in journal.Originals)
        {
            var entry = await transaction.Operations.GetAsync(original.OperationId, token).ConfigureAwait(false);
            if (entry is null) continue;
            if (entry.Kind != CloudStateOperationKind.Delete || entry.ItemId != original.ItemId || entry.Sequence != original.Sequence)
                throw new InvalidDataException("An original directory journal entry changed.");
            pending++;
        }
        await transaction.RollbackAsync(token).ConfigureAwait(false);
        return pending;
    }
}
