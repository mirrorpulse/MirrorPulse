using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

// Rejects an actual public conflict-removal commit after the original item tombstone.
// All SDK state, private recovery checkpoints and SQLite ownership remain with CfSharp.
internal sealed class MirrorPulseRemoteConflictCommitFault
{
    private Guid _conflictId, _itemId;
    private string? _revision;
    private int _failed;
    internal int Failures => Volatile.Read(ref _failed);
    internal void Arm(Guid conflictId, Guid itemId, string? revision) => (_conflictId, _itemId, _revision) = (conflictId, itemId, revision);
    internal ICloudStateStoreFactory Wrap(ICloudStateStoreFactory inner) => new Factory(inner, this);

    private async ValueTask CommitAsync(ICloudStateTransaction transaction, CancellationToken token)
    {
        if (_conflictId != Guid.Empty && Volatile.Read(ref _failed) == 0 &&
            await transaction.Conflicts.GetAsync(_conflictId, token).ConfigureAwait(false) is null &&
            await transaction.Items.GetByItemIdAsync(_itemId, token).ConfigureAwait(false) is { IsTombstone: true } item &&
            item.RemoteRevision == _revision && Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            throw new IOException("Synthetic loss of the explicit SDK conflict-removal commit after its actual tombstone.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private sealed class Factory(ICloudStateStoreFactory inner, MirrorPulseRemoteConflictCommitFault fault) : ICloudStateStoreFactory
    {
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new Store(await inner.OpenAsync(context, cancellationToken).ConfigureAwait(false), fault);
    }
    private sealed class Store(ICloudStateStore inner, MirrorPulseRemoteConflictCommitFault fault) : ICloudStateStore
    {
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), fault);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Transaction(ICloudStateTransaction inner, MirrorPulseRemoteConflictCommitFault fault) : ICloudStateTransaction
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
