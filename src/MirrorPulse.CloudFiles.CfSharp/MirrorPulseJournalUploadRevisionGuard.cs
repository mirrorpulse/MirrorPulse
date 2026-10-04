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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootRelativePath);

        await using ICloudStateTransaction transaction = await state.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);
        CloudItemState? item;
        if (command.ItemId is { } itemId)
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
            command.InstanceId, remoteStatPath ?? command.RelativePath), cancellationToken).ConfigureAwait(false);
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
