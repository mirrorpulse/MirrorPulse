using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

// Decorates only public state-store contracts. The official SQLite provider still
// performs every write, commit and rollback; no SDK payload or schema is decoded.
internal sealed class MirrorPulseRemoteProgressCommitFault(CloudRemoteChangeBatch batch)
{
    private int _failed;
    internal int Failures => Volatile.Read(ref _failed);
    internal CloudRemoteBatchState? UncommittedProgress { get; private set; }
    internal ICloudStateStoreFactory Wrap(ICloudStateStoreFactory inner) => new Factory(inner, this);

    private async ValueTask CommitAsync(ICloudStateTransaction transaction, CancellationToken token)
    {
        if (Volatile.Read(ref _failed) == 0)
        {
            var progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, token);
            var change = batch.Changes[0];
            if (progress is { AppliedEntryCount: 1, Status: CloudRemoteBatchStatus.Applied } &&
                progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) && progress.LastAppliedChangeId == change.ChangeId &&
                change.ItemId is { } itemId)
            {
                var item = await transaction.Items.GetByItemIdAsync(itemId, token);
                if (item is not null && item.RelativePath.Replace('\\', '/') == change.RelativePath.Replace('\\', '/') &&
                    item.RemoteRevision == change.RemoteRevision && Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
                {
                    UncommittedProgress = progress;
                    throw new IOException("Synthetic loss of the original SDK batch-progress commit after item projection.");
                }
            }
        }
        await transaction.CommitAsync(token);
    }

    private sealed class Factory(ICloudStateStoreFactory inner, MirrorPulseRemoteProgressCommitFault fault) : ICloudStateStoreFactory
    {
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new Store(await inner.OpenAsync(context, cancellationToken), fault);
    }

    private sealed class Store(ICloudStateStore inner, MirrorPulseRemoteProgressCommitFault fault) : ICloudStateStore
    {
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), fault);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class Transaction(ICloudStateTransaction inner, MirrorPulseRemoteProgressCommitFault fault) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => inner.Items;
        public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
        public ICloudOperationJournal Operations => inner.Operations;
        public ICloudConflictRepository Conflicts => inner.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
        public ValueTask CommitAsync(CancellationToken cancellationToken = default) => fault.CommitAsync(inner, cancellationToken);
        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
