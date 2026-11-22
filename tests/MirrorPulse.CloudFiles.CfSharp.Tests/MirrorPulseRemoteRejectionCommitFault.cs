using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

/// <summary>Interrupts one real original SDK transaction before or after its actual SQLite commit.</summary>
internal sealed class MirrorPulseRemoteRejectionCommitFault(CloudRemoteChangeBatch batch, bool afterCommit)
{
    private int _failures;
    internal int Failures => Volatile.Read(ref _failures);
    internal ICloudStateStoreFactory Wrap(ICloudStateStoreFactory inner) => new Factory(inner, this);

    private async ValueTask CommitAsync(ICloudStateTransaction transaction, CancellationToken token)
    {
        bool interrupt = false;
        if (Volatile.Read(ref _failures) == 0)
        {
            var progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, token).ConfigureAwait(false);
            interrupt = progress is { AppliedEntryCount: 1, Status: CloudRemoteBatchStatus.Applied } &&
                progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span) &&
                progress.LastAppliedChangeId == batch.Changes[0].ChangeId && Interlocked.CompareExchange(ref _failures, 1, 0) == 0;
        }
        if (interrupt && !afterCommit) throw new IOException("Synthetic interruption before the original SDK rejection commit.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
        if (interrupt) throw new IOException("Synthetic interruption after the actual original SDK rejection commit.");
    }

    private sealed class Factory(ICloudStateStoreFactory inner, MirrorPulseRemoteRejectionCommitFault fault) : ICloudStateStoreFactory
    {
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new Store(await inner.OpenAsync(context, cancellationToken).ConfigureAwait(false), fault);
    }
    private sealed class Store(ICloudStateStore inner, MirrorPulseRemoteRejectionCommitFault fault) : ICloudStateStore
    {
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), fault);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class Transaction(ICloudStateTransaction inner, MirrorPulseRemoteRejectionCommitFault fault) : ICloudStateTransaction
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
