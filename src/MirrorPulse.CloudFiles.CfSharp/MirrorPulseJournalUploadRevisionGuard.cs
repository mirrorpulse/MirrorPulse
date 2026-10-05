using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>
/// Uses CfSharp's last mutually acknowledged revision as the conditional upload precondition.
/// A fresh remote Stat is only a conflict check; it must never become the expected revision.
/// </summary>
public static class MirrorPulseJournalUploadRevisionGuard
{
    public static async ValueTask<string?> ResolveAsync(
        ICloudStateStore state,
        IMirrorPulseWorkerStatTransport stats,
        MirrorPulseWorkerChangeCommand command,
        string syncRootRelativePath,
        string? remoteStatPath = null,
        bool allowTombstone = false,
        bool allowMissingRemote = false,
        string? remoteStatRootKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootRelativePath);

        await using ICloudStateTransaction transaction = await state.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);
        Guid? referencedItem = command.ItemId;
        CloudOperationJournalEntry? currentOperation = await transaction.Operations.GetAsync(command.OperationId, cancellationToken).ConfigureAwait(false);
        if (currentOperation is not null && currentOperation.ItemId != referencedItem)
        {
            // An earlier command in this same batch can finish official identity
            // projection. Re-read only the journal's current reference, preserving
            // the operation and its immutable routing/proof rather than adopting
            // an arbitrary unknown ID from the current path.
            CloudStateOperationKind? expectedKind = command.Kind switch
            {
                MirrorPulseWorkerChangeKind.Create => CloudStateOperationKind.Create,
                MirrorPulseWorkerChangeKind.ContentUpdate => CloudStateOperationKind.ContentUpdate,
                MirrorPulseWorkerChangeKind.MetadataUpdate => CloudStateOperationKind.MetadataUpdate,
                MirrorPulseWorkerChangeKind.Move => CloudStateOperationKind.Move,
                MirrorPulseWorkerChangeKind.Delete => CloudStateOperationKind.Delete,
                _ => null,
            };
            if (currentOperation.Sequence != command.Sequence || currentOperation.Kind != expectedKind)
                throw new MirrorPulseMutationAmbiguousException("The authoritative journal operation changed during dispatch.");
            referencedItem = currentOperation.ItemId;
        }
        CloudItemState? item;
        if (referencedItem is { } itemId)
            item = await transaction.Items.GetByItemIdAsync(itemId, cancellationToken).ConfigureAwait(false);
        else
        {
            // CfSharp stores canonical native paths; older product projections may
            // use portable separators. Resolve both without adopting an unknown ID.
            string nativePath = syncRootRelativePath.Replace('/', Path.DirectorySeparatorChar);
            item = await transaction.Items.GetByRelativePathAsync(nativePath, cancellationToken).ConfigureAwait(false);
            if (nativePath != syncRootRelativePath)
            {
                CloudItemState? portable = await transaction.Items.GetByRelativePathAsync(syncRootRelativePath, cancellationToken).ConfigureAwait(false);
                if (item is not null && portable is not null && item.ItemId != portable.ItemId)
                    throw new MirrorPulseMutationAmbiguousException("The native and portable state paths identify different objects.");
                item ??= portable;
            }
        }
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        if (item?.IsTombstone == true && !allowTombstone)
        {
            throw new MirrorPulseUploadConflictException(null, null);
        }

        string? expected = string.IsNullOrEmpty(item?.RemoteRevision) ? null : item.RemoteRevision;
        string? actual = await stats.StatAsync(new MirrorPulseWorkerStatRequest(
            command.InstanceId, remoteStatPath ?? command.RelativePath, remoteStatRootKey ?? command.RootKey), cancellationToken).ConfigureAwait(false);
        actual = string.IsNullOrEmpty(actual) ? null : actual;
        if (allowMissingRemote && actual is null)
        {
            return expected;
        }

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new MirrorPulseUploadConflictException(expected, actual);
        }

        return expected;
    }
}

public sealed class MirrorPulseUploadConflictException : IOException
{
    public MirrorPulseUploadConflictException(string? expectedRevision, string? actualRevision)
        : base("The remote file changed since the last mutually acknowledged revision.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string? ExpectedRevision { get; }

    public string? ActualRevision { get; }
}
